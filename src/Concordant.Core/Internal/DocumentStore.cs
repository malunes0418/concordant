using System.Text;
using Concordant.Internal.Normalization;
using Concordant.Internal.Sequences;
using Concordant.Values;

namespace Concordant.Internal;

/// <summary>
/// Production operation store matching the reference oracle integration rules.
/// Caller-serialized; reentrancy is enforced by <see cref="ConcordantDocument"/>.
/// </summary>
internal sealed class DocumentStore
{
    private readonly ConcordantDocumentOptions _options;
    private readonly Action<ConcordantWarning>? _warningHandler;

    private readonly Dictionary<OpId, ConcordantOperation> _ops = new();
    private readonly Dictionary<SessionId, ulong> _frontier = new();
    private readonly Dictionary<SessionId, ulong> _sessionLamport = new();
    private readonly PendingIndex _pending = new();
    private long _pendingBytes;

    private readonly Dictionary<string, RootState> _roots = new(StringComparer.Ordinal);
    private readonly Dictionary<OpId, NestedState> _nested = new();
    private readonly Dictionary<(ContainerRef Map, string Key), List<MapAssignment>> _maps = new();
    private readonly Dictionary<ContainerRef, YataSequence> _sequences = new();
    private readonly Dictionary<OpId, ContainerRef> _seqItemOwner = new();

    private Action<ConcordantWarning>? _activeWarningSink;
    private StoreSnapshot? _transactionSnapshot;

    private sealed class RootState
    {
        public required RootKind Kind { get; set; }
        public required OpId DeclarationId { get; set; }
        public bool Conflict { get; set; }
    }

    private sealed class NestedState
    {
        public required RootKind Kind { get; init; }
        public required OpId ParentOpId { get; init; }
        public required int Depth { get; init; }
    }

    private sealed class MapAssignment
    {
        public required OpId Id { get; init; }
        public required ulong Lamport { get; init; }
        public required ConcordantContent Value { get; init; }
    }

    private sealed class StoreSnapshot
    {
        public required Dictionary<OpId, ConcordantOperation> Ops { get; init; }
        public required Dictionary<SessionId, ulong> Frontier { get; init; }
        public required Dictionary<SessionId, ulong> SessionLamport { get; init; }
        public required PendingIndex Pending { get; init; }
        public required long PendingBytes { get; init; }
        public required Dictionary<string, RootState> Roots { get; init; }
        public required Dictionary<OpId, NestedState> Nested { get; init; }
        public required Dictionary<(ContainerRef Map, string Key), List<MapAssignment>> Maps { get; init; }
        public required Dictionary<ContainerRef, YataSequence> Sequences { get; init; }
        public required Dictionary<OpId, ContainerRef> SeqItemOwner { get; init; }
    }

    public DocumentStore(ConcordantDocumentOptions options)
    {
        _options = options;
        _warningHandler = options.WarningHandler;
    }

    public IReadOnlyDictionary<SessionId, ulong> StateVector => _frontier;

    public IReadOnlyDictionary<OpId, ConcordantOperation> Operations => _ops;

    public int PendingCount => _pending.Count;

    public bool IsEmpty => _ops.Count == 0 && _pending.Count == 0;

    /// <summary>Begins a local transaction checkpoint for all-or-nothing commit semantics.</summary>
    public void BeginLocalTransaction()
    {
        if (_transactionSnapshot is not null)
        {
            throw new InvalidOperationException("A local transaction checkpoint is already active.");
        }

        _transactionSnapshot = CaptureSnapshot();
    }

    /// <summary>Commits the active local transaction checkpoint (discards the rollback image).</summary>
    public void CommitLocalTransaction()
    {
        _transactionSnapshot = null;
    }

    /// <summary>Rolls back document state to the active local transaction checkpoint.</summary>
    public void RollbackLocalTransaction()
    {
        if (_transactionSnapshot is null)
        {
            return;
        }

        RestoreSnapshot(_transactionSnapshot);
        _transactionSnapshot = null;
    }

    /// <summary>All integrated operations in deterministic OpId order.</summary>
    public IReadOnlyList<ConcordantOperation> GetIntegratedOperations()
    {
        if (_ops.Count == 0)
        {
            return Array.Empty<ConcordantOperation>();
        }

        return _ops.Values.OrderBy(o => o.Id).ToArray();
    }

    /// <summary>
    /// Integrated operations with clock strictly greater than <paramref name="remoteStateVector"/> per session.
    /// </summary>
    public IReadOnlyList<ConcordantOperation> GetOperationsSince(IReadOnlyDictionary<SessionId, ulong> remoteStateVector)
    {
        ArgumentNullException.ThrowIfNull(remoteStateVector);
        if (_ops.Count == 0)
        {
            return Array.Empty<ConcordantOperation>();
        }

        var result = new List<ConcordantOperation>();
        foreach (ConcordantOperation op in _ops.Values.OrderBy(o => o.Id))
        {
            ulong remote = remoteStateVector.TryGetValue(op.Id.Session, out ulong n) ? n : 0UL;
            if (op.Id.Clock > remote)
            {
                result.Add(op);
            }
        }

        return result;
    }

    public IReadOnlyDictionary<SessionId, ulong> SnapshotStateVector() =>
        new Dictionary<SessionId, ulong>(_frontier);

    public IReadOnlyList<MissingClockRange> ComputeMissingRanges()
    {
        if (_pending.Count == 0)
        {
            return Array.Empty<MissingClockRange>();
        }

        var ranges = new List<MissingClockRange>();
        foreach (SessionId session in _pending.Sessions)
        {
            if (!_pending.TryGetSessionMinClock(session, out ulong minPending))
            {
                continue;
            }

            ulong frontier = _frontier.TryGetValue(session, out ulong n) ? n : 0UL;
            if (minPending > frontier + 1)
            {
                ranges.Add(new MissingClockRange(session, frontier + 1, minPending - 1));
            }
        }

        foreach (ConcordantOperation op in _pending.Operations)
        {
            if (op.LamportSource is OpId src
                && !_ops.ContainsKey(src)
                && !_pending.Contains(src))
            {
                ranges.Add(new MissingClockRange(src.Session, src.Clock, src.Clock));
            }
        }

        return ranges
            .GroupBy(r => (r.Session, r.FromClockInclusive, r.ToClockInclusive))
            .Select(g => g.First())
            .OrderBy(r => r.Session)
            .ThenBy(r => r.FromClockInclusive)
            .ToArray();
    }

    public ApplyResult Apply(OperationBatch batch, Action<ConcordantWarning>? warningSink = null)
    {
        ArgumentNullException.ThrowIfNull(batch);
        Action<ConcordantWarning>? previousSink = _activeWarningSink;
        _activeWarningSink = warningSink;
        try
        {
            return ApplyCore(batch);
        }
        finally
        {
            _activeWarningSink = previousSink;
        }
    }

    private ApplyResult ApplyCore(OperationBatch batch)
    {
        NormalizeBatchResult normalized = AlwaysSafeNormalizer.NormalizeBatch(batch.Operations);
        if (normalized.Status == NormalizeBatchStatus.ReplicaFork)
        {
            return ApplyResult.Rejected(
                $"ReplicaFork at {normalized.ForkId}",
                ApplyRejectReason.ReplicaFork,
                SnapshotStateVector());
        }

        IReadOnlyList<ConcordantOperation> incoming = normalized.Operations;

        bool allKnown = true;
        foreach (ConcordantOperation op in incoming)
        {
            if (!_ops.TryGetValue(op.Id, out ConcordantOperation? existing))
            {
                allKnown = false;
                break;
            }

            if (!OperationEquality.AreEqual(existing, op))
            {
                return ApplyResult.Rejected(
                    $"ReplicaFork at {op.Id}",
                    ApplyRejectReason.ReplicaFork,
                    SnapshotStateVector());
            }
        }

        if (allKnown)
        {
            return ApplyResult.Duplicate(SnapshotStateVector());
        }

        var staged = new List<ConcordantOperation>();
        foreach (ConcordantOperation op in incoming)
        {
            if (_ops.TryGetValue(op.Id, out ConcordantOperation? existing))
            {
                if (!OperationEquality.AreEqual(existing, op))
                {
                    return ApplyResult.Rejected(
                        $"ReplicaFork at {op.Id}",
                        ApplyRejectReason.ReplicaFork,
                        SnapshotStateVector());
                }

                continue;
            }

            if (_pending.TryGet(op.Id, out ConcordantOperation pendingOp))
            {
                if (!OperationEquality.AreEqual(pendingOp, op))
                {
                    return ApplyResult.Rejected(
                        $"ReplicaFork at {op.Id}",
                        ApplyRejectReason.ReplicaFork,
                        SnapshotStateVector());
                }

                continue;
            }

            staged.Add(op);
        }

        if (staged.Count == 0)
        {
            _ = NormalizePending();
            return ApplyResult.Duplicate(SnapshotStateVector());
        }

        foreach (IGrouping<SessionId, ConcordantOperation> group in staged.GroupBy(o => o.Id.Session))
        {
            List<ConcordantOperation> ordered = group.OrderBy(o => o.Id.Clock).ToList();
            for (int i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].Id.Clock != ordered[i - 1].Id.Clock + 1)
                {
                    return ApplyResult.Rejected(
                        $"Non-contiguous clocks in batch for session {group.Key}",
                        ApplyRejectReason.Invalid,
                        SnapshotStateVector());
                }
            }
        }

        foreach (ConcordantOperation op in staged)
        {
            if (!ValidatePayload(op, out ApplyResult? reject))
            {
                return AttachStateVector(reject!);
            }
        }

        long stagedBytes = staged.Sum(EstimateBytes);
        if (_pending.Count + staged.Count > _options.MaxPendingOperations
            || _pendingBytes + stagedBytes > _options.MaxPendingBytes)
        {
            return ApplyResult.Rejected(
                "Pending quota exceeded.",
                ApplyRejectReason.QuotaExceeded,
                SnapshotStateVector(),
                retryable: true);
        }

        foreach (ConcordantOperation op in staged)
        {
            ulong frontier = _frontier.TryGetValue(op.Id.Session, out ulong n) ? n : 0UL;
            if (op.Id.Clock > frontier && op.Id.Clock - frontier > (ulong)_options.MaxClockGap)
            {
                return ApplyResult.Rejected(
                    $"Clock gap exceeds MaxClockGap for session {op.Id.Session}.",
                    ApplyRejectReason.QuotaExceeded,
                    SnapshotStateVector(),
                    retryable: true);
            }
        }

        if (!TryPreflightRetentionQuotas(staged, out ApplyResult? quotaReject))
        {
            return AttachStateVector(quotaReject!);
        }

        // Snapshot so a deferred retention-quota failure cannot leave partial integrations
        // or permanently pending quota-blocked ops. Inside an active local transaction the
        // outer transaction checkpoint already covers rollback.
        StoreSnapshot? applySnapshot = _transactionSnapshot is null ? CaptureSnapshot() : null;

        foreach (ConcordantOperation op in staged)
        {
            _pending.Add(op);
            _pendingBytes += EstimateBytes(op);
        }

        if (!IntegratePending(out string? integrateError))
        {
            if (applySnapshot is not null)
            {
                RestoreSnapshot(applySnapshot);
            }
            else
            {
                throw new InvalidOperationException(
                    integrateError ?? "Retention quota exceeded during integration.");
            }

            return ApplyResult.Rejected(
                integrateError ?? "Retention quota exceeded during integration.",
                ApplyRejectReason.QuotaExceeded,
                SnapshotStateVector(),
                retryable: true);
        }

        bool anyIntegrated = staged.Any(o => _ops.ContainsKey(o.Id));
        bool anyPending = staged.Any(o => _pending.Contains(o.Id));
        if (anyPending && !anyIntegrated)
        {
            return ApplyResult.Pending(
                "Waiting for causal dependencies.",
                SnapshotStateVector(),
                ComputeMissingRanges());
        }

        return ApplyResult.Integrated(SnapshotStateVector());
    }

    /// <summary>
    /// Always-safe pending compaction. Never removes tombstones or integrated history.
    /// </summary>
    public int NormalizePending()
    {
        int removed = 0;
        List<OpId>? toRemove = null;
        foreach (ConcordantOperation op in _pending.Operations)
        {
            if (!_ops.ContainsKey(op.Id))
            {
                continue;
            }

            toRemove ??= new List<OpId>();
            toRemove.Add(op.Id);
        }

        if (toRemove is null)
        {
            return 0;
        }

        foreach (OpId id in toRemove)
        {
            if (_pending.Remove(id, out ConcordantOperation op))
            {
                _pendingBytes -= EstimateBytes(op);
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// Preflight quotas that would otherwise fail during integrate.
    /// Rejects atomically before pending mutation.
    /// Retention quotas are always budgeted so ops cannot stick permanently pending.
    /// </summary>
    private bool TryPreflightRetentionQuotas(List<ConcordantOperation> staged, out ApplyResult? reject)
    {
        reject = null;

        foreach (ConcordantOperation op in staged)
        {
            ContainerRef? parent = op switch
            {
                ConcordantOperation.MapSet { Value: ConcordantContent.NestedContent } m => m.Map,
                ConcordantOperation.SeqInsert { Content: ConcordantContent.NestedContent } s => s.Container,
                _ => null,
            };

            if (parent is null || TryGetContainerKind(parent.Value) is null)
            {
                continue;
            }

            if (GetDepth(parent.Value) + 1 > _options.MaxNestingDepth)
            {
                reject = ApplyResult.Rejected(
                    "MaxNestingDepth exceeded.",
                    ApplyRejectReason.QuotaExceeded,
                    retryable: true);
                return false;
            }
        }

        // Budget eventual integration of every pending + staged op, including clocks that must
        // appear to fill gaps before a pending head can integrate. This prevents ops from sticking
        // permanently pending after MaxOperations / MaxHistoricalSessions.
        int missingGapOps = CountMissingGapOperations(staged);
        if (_ops.Count + _pending.Count + staged.Count + missingGapOps > _options.MaxOperations)
        {
            reject = ApplyResult.Rejected(
                "MaxOperations exceeded.",
                ApplyRejectReason.QuotaExceeded,
                retryable: true);
            return false;
        }

        var sessions = new HashSet<SessionId>(_frontier.Keys);
        foreach (SessionId session in _pending.Sessions)
        {
            _ = sessions.Add(session);
        }

        foreach (ConcordantOperation op in staged)
        {
            _ = sessions.Add(op.Id.Session);
        }

        if (sessions.Count > _options.MaxHistoricalSessions)
        {
            reject = ApplyResult.Rejected(
                "MaxHistoricalSessions exceeded.",
                ApplyRejectReason.QuotaExceeded,
                retryable: true);
            return false;
        }

        return true;
    }

    private int CountMissingGapOperations(List<ConcordantOperation> staged)
    {
        var minPending = new Dictionary<SessionId, ulong>();
        foreach (SessionId session in _pending.Sessions)
        {
            if (_pending.TryGetSessionMinClock(session, out ulong min))
            {
                minPending[session] = min;
            }
        }

        foreach (ConcordantOperation op in staged)
        {
            if (!minPending.TryGetValue(op.Id.Session, out ulong existing) || op.Id.Clock < existing)
            {
                minPending[op.Id.Session] = op.Id.Clock;
            }
        }

        int missing = 0;
        foreach (KeyValuePair<SessionId, ulong> entry in minPending)
        {
            ulong frontier = _frontier.TryGetValue(entry.Key, out ulong n) ? n : 0UL;
            if (entry.Value > frontier + 1)
            {
                missing += (int)(entry.Value - frontier - 1);
            }
        }

        return missing;
    }

    private ApplyResult AttachStateVector(ApplyResult result) =>
        new(
            result.Status,
            result.Detail,
            result.Reason,
            SnapshotStateVector(),
            result.MissingRanges,
            result.Warnings,
            result.Retryable,
            result.CodecVersion,
            result.RequiredFeatures);

    public RootKind? TryGetRootKind(string name) =>
        _roots.TryGetValue(name, out RootState? root) ? root.Kind : null;

    public bool HasRootConflict(string name) =>
        _roots.TryGetValue(name, out RootState? root) && root.Conflict;

    public RootKind? TryGetContainerKind(ContainerRef container)
    {
        if (container.IsRoot)
        {
            return TryGetRootKind(container.RootName!);
        }

        return _nested.TryGetValue(container.NestedId!.Value, out NestedState? n) ? n.Kind : null;
    }

    public IReadOnlyDictionary<string, ConcordantContent> VisibleMap(ContainerRef map)
    {
        var result = new SortedDictionary<string, ConcordantContent>(StringComparer.Ordinal);
        foreach (KeyValuePair<(ContainerRef Map, string Key), List<MapAssignment>> entry in _maps)
        {
            if (!entry.Key.Map.Equals(map))
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

    public YataSequence? TryGetSequence(ContainerRef container) =>
        _sequences.TryGetValue(container, out YataSequence? seq) ? seq : null;

    /// <summary>Looks up a sequence item (including tombstones) and its container.</summary>
    public bool TryGetSeqItem(OpId id, out ContainerRef container, out SeqItem item)
    {
        if (_seqItemOwner.TryGetValue(id, out ContainerRef owner)
            && _sequences.TryGetValue(owner, out YataSequence? seq)
            && seq.TryGetNode(id, out LinkedListNode<SeqItem> node))
        {
            container = owner;
            item = node.Value;
            return true;
        }

        container = default;
        item = null!;
        return false;
    }

    /// <summary>Current map register winner for <paramref name="key"/>, if any assignment exists.</summary>
    public bool TryGetMapWinner(ContainerRef map, string key, out OpId id, out ConcordantContent value)
    {
        if (_maps.TryGetValue((map, key), out List<MapAssignment>? list) && list.Count > 0)
        {
            MapAssignment winner = list
                .OrderByDescending(a => a.Lamport)
                .ThenByDescending(a => a.Id)
                .First();
            id = winner.Id;
            value = winner.Value;
            return true;
        }

        id = default;
        value = null!;
        return false;
    }

    /// <summary>
    /// Best assignment for <paramref name="key"/> excluding <paramref name="excludeId"/> (runner-up / prior value).
    /// </summary>
    public bool TryGetMapRunnerUp(
        ContainerRef map,
        string key,
        OpId excludeId,
        out OpId id,
        out ConcordantContent value)
    {
        if (_maps.TryGetValue((map, key), out List<MapAssignment>? list))
        {
            MapAssignment? best = null;
            foreach (MapAssignment a in list)
            {
                if (a.Id == excludeId)
                {
                    continue;
                }

                if (best is null
                    || a.Lamport > best.Lamport
                    || (a.Lamport == best.Lamport && a.Id.CompareTo(best.Id) > 0))
                {
                    best = a;
                }
            }

            if (best is not null)
            {
                id = best.Id;
                value = best.Value;
                return true;
            }
        }

        id = default;
        value = null!;
        return false;
    }

    public string VisibleText(ContainerRef container)
    {
        if (!_sequences.TryGetValue(container, out YataSequence? seq))
        {
            return string.Empty;
        }

        return seq.BuildVisibleText();
    }

    /// <summary>Canonical fingerprint of visible state for oracle convergence assertions.</summary>
    public string VisibleFingerprint()
    {
        var sb = new StringBuilder();

        ContainerRef textRoot = ContainerRef.Root("text");
        sb.Append("text=").Append(VisibleText(textRoot)).Append('|');

        sb.Append("roots=");
        foreach (KeyValuePair<string, RootState> root in _roots.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            sb.Append(root.Key).Append(':').Append(root.Value.Kind)
                .Append(root.Value.Conflict ? '!' : '.')
                .Append(';');
        }

        sb.Append("|maps=");
        foreach (ContainerRef mapRef in _maps.Keys.Select(k => k.Map).Distinct().OrderBy(m => m.FingerprintKey(), StringComparer.Ordinal))
        {
            if (!mapRef.IsRoot)
            {
                continue;
            }

            sb.Append(mapRef.RootName).Append('{');
            foreach (KeyValuePair<string, ConcordantContent> kv in VisibleMap(mapRef))
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

    private bool IntegratePending(out string? error)
    {
        error = null;
        _ = NormalizePending();

        SeedReadyQueue();

        while (_pending.TryDequeueReady(out ConcordantOperation op))
        {
            if (_ops.ContainsKey(op.Id))
            {
                if (_pending.Remove(op.Id, out ConcordantOperation removed))
                {
                    _pendingBytes -= EstimateBytes(removed);
                }

                continue;
            }

            if (!CanIntegrate(op))
            {
                RegisterDependencies(op);
                continue;
            }

            try
            {
                IntegrateOne(op);
            }
            catch (OverflowException)
            {
                // Leave in pending; do not create silent clock holes.
                RegisterDependencies(op);
                continue;
            }
            catch (InvalidOperationException ex)
            {
                // Retention / structural quota: do not leave permanently pending.
                if (IsRetentionQuotaMessage(ex.Message))
                {
                    error = ex.Message;
                    return false;
                }

                RegisterDependencies(op);
                continue;
            }

            if (_pending.Remove(op.Id, out ConcordantOperation integrated))
            {
                _pendingBytes -= EstimateBytes(integrated);
            }

            OnIntegrated(op);
        }

        return true;
    }

    private static bool IsRetentionQuotaMessage(string message) =>
        message.Contains("MaxOperations", StringComparison.Ordinal)
        || message.Contains("MaxHistoricalSessions", StringComparison.Ordinal);

    private void SeedReadyQueue()
    {
        foreach (ConcordantOperation op in _pending.Operations)
        {
            if (CanIntegrate(op))
            {
                _pending.EnqueueReady(op.Id);
            }
            else
            {
                RegisterDependencies(op);
            }
        }
    }

    private void RegisterDependencies(ConcordantOperation op)
    {
        ulong expectedClock = _frontier.TryGetValue(op.Id.Session, out ulong n) ? n + 1 : 1UL;
        if (op.Id.Clock != expectedClock)
        {
            // Wait for the predecessor clock in this session (if present in pending/integrated).
            if (op.Id.Clock > 1)
            {
                var pred = new OpId(op.Id.Session, op.Id.Clock - 1);
                if (!_ops.ContainsKey(pred))
                {
                    _pending.RegisterWaiter(pred, op.Id);
                }
            }
        }

        if (op.LamportSource is OpId source && !_ops.ContainsKey(source))
        {
            _pending.RegisterWaiter(source, op.Id);
        }

        switch (op)
        {
            case ConcordantOperation.SeqInsert insert:
                if (insert.LeftOrigin is OpId left && !_ops.ContainsKey(left))
                {
                    _pending.RegisterWaiter(left, op.Id);
                }

                if (insert.RightOrigin is OpId right && !_ops.ContainsKey(right))
                {
                    _pending.RegisterWaiter(right, op.Id);
                }

                break;
            case ConcordantOperation.SeqDelete delete:
                if (!_seqItemOwner.ContainsKey(delete.TargetId))
                {
                    _pending.RegisterWaiter(delete.TargetId, op.Id);
                }

                break;
        }
    }

    private void OnIntegrated(ConcordantOperation op)
    {
        _pending.NotifyIntegrated(op.Id, id => _pending.EnqueueReady(id));

        // Next contiguous clock for this session may now be ready.
        ulong nextClock = op.Id.Clock + 1;
        if (_pending.TryGetBySessionClock(op.Id.Session, nextClock, out ConcordantOperation next))
        {
            _pending.EnqueueReady(next.Id);
        }
    }

    private bool CanIntegrate(ConcordantOperation op)
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

        ulong expectedLamport;
        try
        {
            expectedLamport = checked(Math.Max(previousLamport, sourceLamport) + 1);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (op.Lamport != expectedLamport)
        {
            return false;
        }

        switch (op)
        {
            case ConcordantOperation.SeqInsert insert:
                if (!OriginsPresent(insert.Container, insert.LeftOrigin, insert.RightOrigin))
                {
                    return false;
                }

                break;
            case ConcordantOperation.SeqDelete delete:
                if (!FindSequenceContaining(delete.TargetId, out _))
                {
                    return false;
                }

                break;
            case ConcordantOperation.MapSet mapSet:
                if (!ContainerExists(mapSet.Map, RootKind.Map))
                {
                    return false;
                }

                break;
        }

        return true;
    }

    private bool OriginsPresent(ContainerRef container, OpId? left, OpId? right)
    {
        if (!_sequences.TryGetValue(container, out YataSequence? seq))
        {
            if (!ContainerExists(container, RootKind.Text) && !ContainerExists(container, RootKind.Array))
            {
                return false;
            }

            if (left is not null || right is not null)
            {
                return false;
            }

            return true;
        }

        if (left is OpId l && !seq.Contains(l))
        {
            return false;
        }

        if (right is OpId r && !seq.Contains(r))
        {
            return false;
        }

        return true;
    }

    private bool ContainerExists(ContainerRef container, RootKind expectedKind)
    {
        RootKind? kind = TryGetContainerKind(container);
        return kind == expectedKind
            || (expectedKind is RootKind.Text or RootKind.Array
                && kind is RootKind.Text or RootKind.Array);
    }

    private bool FindSequenceContaining(OpId targetId, out YataSequence? sequence)
    {
        if (_seqItemOwner.TryGetValue(targetId, out ContainerRef owner)
            && _sequences.TryGetValue(owner, out YataSequence? seq))
        {
            sequence = seq;
            return true;
        }

        sequence = null;
        return false;
    }

    private void IntegrateOne(ConcordantOperation op)
    {
        if (_ops.Count >= _options.MaxOperations)
        {
            throw new InvalidOperationException("MaxOperations exceeded.");
        }

        if (!_frontier.ContainsKey(op.Id.Session) && _frontier.Count >= _options.MaxHistoricalSessions)
        {
            throw new InvalidOperationException("MaxHistoricalSessions exceeded.");
        }

        switch (op)
        {
            case ConcordantOperation.RootDeclare root:
                IntegrateRoot(root);
                break;
            case ConcordantOperation.MapSet mapSet:
                IntegrateMapSet(mapSet);
                break;
            case ConcordantOperation.SeqInsert insert:
                IntegrateSeqInsert(insert);
                break;
            case ConcordantOperation.SeqDelete delete:
                IntegrateSeqDelete(delete);
                break;
            default:
                throw new InvalidOperationException("Unknown operation kind.");
        }

        _ops[op.Id] = op;
        _frontier[op.Id.Session] = op.Id.Clock;
        _sessionLamport[op.Id.Session] = op.Lamport;
    }

    private void IntegrateRoot(ConcordantOperation.RootDeclare op)
    {
        if (!_roots.TryGetValue(op.Name, out RootState? existing))
        {
            _roots[op.Name] = new RootState
            {
                Kind = op.Kind,
                DeclarationId = op.Id,
            };
            EnsureContainerScaffold(ContainerRef.Root(op.Name), op.Kind, depth: 0, parentOpId: op.Id);
            return;
        }

        if (existing.Kind == op.Kind)
        {
            return;
        }

        existing.Conflict = true;
        EmitWarning(new ConcordantWarning(
            ConcordantWarningKind.RootKindConflict,
            $"Root '{op.Name}' has concurrent kind conflict; keeping {existing.Kind} vs {op.Kind}."));

        if (op.Id.CompareTo(existing.DeclarationId) < 0)
        {
            existing.Kind = op.Kind;
            existing.DeclarationId = op.Id;
            EnsureContainerScaffold(ContainerRef.Root(op.Name), op.Kind, depth: 0, parentOpId: op.Id);
        }
    }

    private void IntegrateMapSet(ConcordantOperation.MapSet op)
    {
        var key = (op.Map, op.Key);
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

        if (op.Value is ConcordantContent.NestedContent nested)
        {
            int parentDepth = GetDepth(op.Map);
            int depth = parentDepth + 1;
            if (depth > _options.MaxNestingDepth)
            {
                throw new InvalidOperationException("MaxNestingDepth exceeded.");
            }

            _nested[op.Id] = new NestedState
            {
                Kind = nested.Kind,
                ParentOpId = op.Id,
                Depth = depth,
            };
            EnsureContainerScaffold(ContainerRef.Nested(op.Id), nested.Kind, depth, op.Id);
        }
    }

    private void IntegrateSeqDelete(ConcordantOperation.SeqDelete op)
    {
        if (FindSequenceContaining(op.TargetId, out YataSequence? seq))
        {
            seq!.MarkDeleted(op.TargetId);
        }
    }

    private void IntegrateSeqInsert(ConcordantOperation.SeqInsert op)
    {
        EnsureSequenceContainer(op.Container);
        YataSequence seq = _sequences[op.Container];
        seq.IntegrateInsert(op.Id, op.LeftOrigin, op.RightOrigin, op.Content);
        _seqItemOwner[op.Id] = op.Container;

        if (op.Content is ConcordantContent.NestedContent nested)
        {
            int parentDepth = GetDepth(op.Container);
            int depth = parentDepth + 1;
            if (depth > _options.MaxNestingDepth)
            {
                throw new InvalidOperationException("MaxNestingDepth exceeded.");
            }

            _nested[op.Id] = new NestedState
            {
                Kind = nested.Kind,
                ParentOpId = op.Id,
                Depth = depth,
            };
            EnsureContainerScaffold(ContainerRef.Nested(op.Id), nested.Kind, depth, op.Id);
        }
    }

    private void EnsureSequenceContainer(ContainerRef container)
    {
        RootKind? kind = TryGetContainerKind(container);
        if (kind is null)
        {
            if (container.IsRoot)
            {
                _roots[container.RootName!] = new RootState
                {
                    Kind = RootKind.Text,
                    DeclarationId = new OpId(SessionId.FromSeed(0), 1),
                };
                kind = RootKind.Text;
            }
            else
            {
                throw new InvalidOperationException("Unknown sequence container.");
            }
        }

        if (kind is not (RootKind.Text or RootKind.Array))
        {
            throw new InvalidOperationException($"Container '{container}' is not a sequence.");
        }

        EnsureContainerScaffold(container, kind.Value, GetDepth(container), default);
    }

    private void EnsureContainerScaffold(ContainerRef container, RootKind kind, int depth, OpId parentOpId)
    {
        _ = depth;
        _ = parentOpId;
        if (kind is RootKind.Text or RootKind.Array)
        {
            if (!_sequences.ContainsKey(container))
            {
                _sequences[container] = new YataSequence();
            }
        }
    }

    private int GetDepth(ContainerRef container)
    {
        if (container.IsRoot)
        {
            return 0;
        }

        return _nested.TryGetValue(container.NestedId!.Value, out NestedState? n) ? n.Depth : 0;
    }

    private bool ValidatePayload(ConcordantOperation op, out ApplyResult? reject)
    {
        reject = null;
        switch (op)
        {
            case ConcordantOperation.RootDeclare root:
                if (string.IsNullOrEmpty(root.Name))
                {
                    reject = ApplyResult.Rejected("Root name must be non-empty.");
                    return false;
                }

                break;
            case ConcordantOperation.MapSet mapSet:
                if (string.IsNullOrEmpty(mapSet.Key))
                {
                    reject = ApplyResult.Rejected("Map key must be non-empty.");
                    return false;
                }

                if (!ValidateContent(mapSet.Value, out reject))
                {
                    return false;
                }

                break;
            case ConcordantOperation.SeqInsert insert:
                if (!ValidateContent(insert.Content, out reject))
                {
                    return false;
                }

                break;
        }

        return true;
    }

    private bool ValidateContent(ConcordantContent content, out ApplyResult? reject)
    {
        reject = null;
        if (content is ConcordantContent.ScalarContent { Value: ConcordantScalar.StringScalar s }
            && s.Value.Length > _options.MaxContentUtf16Length)
        {
            reject = ApplyResult.Rejected("Content exceeds MaxContentUtf16Length.", ApplyRejectReason.QuotaExceeded);
            return false;
        }

        return true;
    }

    private void EmitWarning(ConcordantWarning warning)
    {
        Action<ConcordantWarning>? sink = _activeWarningSink ?? _warningHandler;
        if (sink is null)
        {
            return;
        }

        try
        {
            sink(warning);
        }
        catch
        {
            // Observer exception isolation.
        }
    }

    private StoreSnapshot CaptureSnapshot()
    {
        var roots = new Dictionary<string, RootState>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, RootState> entry in _roots)
        {
            roots[entry.Key] = new RootState
            {
                Kind = entry.Value.Kind,
                DeclarationId = entry.Value.DeclarationId,
                Conflict = entry.Value.Conflict,
            };
        }

        var nested = new Dictionary<OpId, NestedState>(_nested);
        var maps = new Dictionary<(ContainerRef Map, string Key), List<MapAssignment>>();
        foreach (KeyValuePair<(ContainerRef Map, string Key), List<MapAssignment>> entry in _maps)
        {
            maps[entry.Key] = entry.Value.ToList();
        }

        var sequences = new Dictionary<ContainerRef, YataSequence>();
        foreach (KeyValuePair<ContainerRef, YataSequence> entry in _sequences)
        {
            sequences[entry.Key] = entry.Value.Clone();
        }

        return new StoreSnapshot
        {
            Ops = new Dictionary<OpId, ConcordantOperation>(_ops),
            Frontier = new Dictionary<SessionId, ulong>(_frontier),
            SessionLamport = new Dictionary<SessionId, ulong>(_sessionLamport),
            Pending = _pending.Clone(),
            PendingBytes = _pendingBytes,
            Roots = roots,
            Nested = nested,
            Maps = maps,
            Sequences = sequences,
            SeqItemOwner = new Dictionary<OpId, ContainerRef>(_seqItemOwner),
        };
    }

    private void RestoreSnapshot(StoreSnapshot snapshot)
    {
        _ops.Clear();
        foreach (KeyValuePair<OpId, ConcordantOperation> entry in snapshot.Ops)
        {
            _ops[entry.Key] = entry.Value;
        }

        _frontier.Clear();
        foreach (KeyValuePair<SessionId, ulong> entry in snapshot.Frontier)
        {
            _frontier[entry.Key] = entry.Value;
        }

        _sessionLamport.Clear();
        foreach (KeyValuePair<SessionId, ulong> entry in snapshot.SessionLamport)
        {
            _sessionLamport[entry.Key] = entry.Value;
        }

        _pending.Clear();
        foreach (ConcordantOperation op in snapshot.Pending.Operations)
        {
            _pending.Add(op);
        }

        _pendingBytes = snapshot.PendingBytes;

        _roots.Clear();
        foreach (KeyValuePair<string, RootState> entry in snapshot.Roots)
        {
            _roots[entry.Key] = entry.Value;
        }

        _nested.Clear();
        foreach (KeyValuePair<OpId, NestedState> entry in snapshot.Nested)
        {
            _nested[entry.Key] = entry.Value;
        }

        _maps.Clear();
        foreach (KeyValuePair<(ContainerRef Map, string Key), List<MapAssignment>> entry in snapshot.Maps)
        {
            _maps[entry.Key] = entry.Value;
        }

        _sequences.Clear();
        foreach (KeyValuePair<ContainerRef, YataSequence> entry in snapshot.Sequences)
        {
            _sequences[entry.Key] = entry.Value;
        }

        _seqItemOwner.Clear();
        foreach (KeyValuePair<OpId, ContainerRef> entry in snapshot.SeqItemOwner)
        {
            _seqItemOwner[entry.Key] = entry.Value;
        }
    }

    private static long EstimateBytes(ConcordantOperation op) => op switch
    {
        ConcordantOperation.RootDeclare r => 32 + (r.Name?.Length ?? 0) * 2L,
        ConcordantOperation.MapSet m => 48 + m.Key.Length * 2L + EstimateContentBytes(m.Value),
        ConcordantOperation.SeqInsert s => 64 + EstimateContentBytes(s.Content),
        ConcordantOperation.SeqDelete => 40,
        _ => 32,
    };

    private static long EstimateContentBytes(ConcordantContent content) => content switch
    {
        ConcordantContent.ScalarContent { Value: ConcordantScalar.StringScalar s } => s.Value.Length * 2L,
        ConcordantContent.ScalarContent => 16,
        ConcordantContent.NestedContent => 8,
        _ => 8,
    };
}
