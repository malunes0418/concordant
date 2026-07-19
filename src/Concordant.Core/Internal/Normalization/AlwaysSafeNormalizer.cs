namespace Concordant.Internal.Normalization;

/// <summary>
/// Always-safe normalization: coalescing and deduplication that preserve Concordant invariants.
/// <para>
/// Guarantees:
/// <list type="bullet">
/// <item>Identical OpId payloads coalesce (dedupe); conflicting OpId payloads surface as forks.</item>
/// <item>Already-integrated operations are stripped from pending without touching history.</item>
/// <item>Tombstones, map assignment history, and dependency-bearing ops are never garbage-collected.</item>
/// <item>Visible fingerprints and integrated OpId sets are unchanged except for pure pending dedupe.</item>
/// </list>
/// </para>
/// Destructive tombstone GC is intentionally out of scope for v1.
/// </summary>
internal static class AlwaysSafeNormalizer
{
    /// <summary>
    /// Deduplicates a batch by <see cref="OpId"/>. Equal payloads coalesce; unequal payloads fork.
    /// Order of first occurrence is preserved.
    /// </summary>
    public static NormalizeBatchResult NormalizeBatch(IReadOnlyList<ConcordantOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count == 0)
        {
            return NormalizeBatchResult.Empty;
        }

        var byId = new Dictionary<OpId, ConcordantOperation>(operations.Count);
        var order = new List<OpId>(operations.Count);
        int coalesced = 0;

        foreach (ConcordantOperation op in operations)
        {
            if (!byId.TryGetValue(op.Id, out ConcordantOperation? existing))
            {
                byId[op.Id] = op;
                order.Add(op.Id);
                continue;
            }

            if (!OperationEquality.AreEqual(existing, op))
            {
                return NormalizeBatchResult.Fork(op.Id);
            }

            coalesced++;
        }

        if (coalesced == 0 && byId.Count == operations.Count)
        {
            return new NormalizeBatchResult(
                NormalizeBatchStatus.Unchanged,
                operations,
                CoalescedDuplicates: 0,
                ForkId: null);
        }

        var normalized = new ConcordantOperation[order.Count];
        for (int i = 0; i < order.Count; i++)
        {
            normalized[i] = byId[order[i]];
        }

        return new NormalizeBatchResult(
            NormalizeBatchStatus.Normalized,
            normalized,
            coalesced,
            ForkId: null);
    }

    /// <summary>
    /// Removes pending entries whose OpIds are already integrated.
    /// Does not remove tombstones or map history.
    /// </summary>
    public static int CompactPending(
        IList<ConcordantOperation> pending,
        IReadOnlyDictionary<OpId, ConcordantOperation> integrated,
        ref long pendingBytes,
        Func<ConcordantOperation, long> estimateBytes)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(integrated);
        ArgumentNullException.ThrowIfNull(estimateBytes);

        int removed = 0;
        for (int i = 0; i < pending.Count;)
        {
            ConcordantOperation op = pending[i];
            if (!integrated.ContainsKey(op.Id))
            {
                i++;
                continue;
            }

            pendingBytes -= estimateBytes(op);
            pending.RemoveAt(i);
            removed++;
        }

        return removed;
    }
}

internal enum NormalizeBatchStatus
{
    Unchanged,
    Normalized,
    ReplicaFork,
}

internal readonly struct NormalizeBatchResult
{
    public NormalizeBatchResult(
        NormalizeBatchStatus status,
        IReadOnlyList<ConcordantOperation> operations,
        int CoalescedDuplicates,
        OpId? ForkId)
    {
        Status = status;
        Operations = operations;
        this.CoalescedDuplicates = CoalescedDuplicates;
        this.ForkId = ForkId;
    }

    public NormalizeBatchStatus Status { get; }

    public IReadOnlyList<ConcordantOperation> Operations { get; }

    public int CoalescedDuplicates { get; }

    public OpId? ForkId { get; }

    public static NormalizeBatchResult Empty { get; } =
        new(NormalizeBatchStatus.Unchanged, Array.Empty<ConcordantOperation>(), 0, null);

    public static NormalizeBatchResult Fork(OpId id) =>
        new(NormalizeBatchStatus.ReplicaFork, Array.Empty<ConcordantOperation>(), 0, id);
}

/// <summary>Shared structural equality for operations (payload + Lamport metadata).</summary>
internal static class OperationEquality
{
    public static bool AreEqual(ConcordantOperation a, ConcordantOperation b)
    {
        if (a.Id != b.Id || a.Lamport != b.Lamport || a.LamportSource != b.LamportSource)
        {
            return false;
        }

        return (a, b) switch
        {
            (ConcordantOperation.RootDeclare ra, ConcordantOperation.RootDeclare rb) =>
                string.Equals(ra.Name, rb.Name, StringComparison.Ordinal) && ra.Kind == rb.Kind,
            (ConcordantOperation.MapSet ma, ConcordantOperation.MapSet mb) =>
                ma.Map == mb.Map
                && string.Equals(ma.Key, mb.Key, StringComparison.Ordinal)
                && ma.Value.CanonicalKey() == mb.Value.CanonicalKey(),
            (ConcordantOperation.SeqInsert sa, ConcordantOperation.SeqInsert sb) =>
                sa.Container == sb.Container
                && sa.LeftOrigin == sb.LeftOrigin
                && sa.RightOrigin == sb.RightOrigin
                && sa.Content.CanonicalKey() == sb.Content.CanonicalKey(),
            (ConcordantOperation.SeqDelete da, ConcordantOperation.SeqDelete db) => da.TargetId == db.TargetId,
            _ => false,
        };
    }
}
