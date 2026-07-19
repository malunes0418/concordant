using Concordant.Model.Tests.ReferenceModel;

namespace Concordant.Model.Tests.Simulation;

/// <summary>
/// Deterministic multi-replica simulator: partitions, reordering, and duplicate delivery.
/// Convergence of visible fingerprints is the Phase 1 gate.
/// </summary>
public sealed class ReplicaSimulator
{
    private readonly List<Replica> _replicas = new();
    private readonly List<RefBatch> _allBatches = new();
    private readonly Random _random;

    public ReplicaSimulator(int replicaCount, int seed)
    {
        if (replicaCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(replicaCount));
        }

        _random = new Random(seed);
        Seed = seed;
        for (int i = 0; i < replicaCount; i++)
        {
            var session = SessionId.FromSeed((ulong)(seed * 1000L + i + 1));
            _replicas.Add(new Replica(session, new ReferenceDocument(), new LocalWriter(session)));
        }
    }

    public int Seed { get; }

    public IReadOnlyList<Replica> Replicas => _replicas;

    public IReadOnlyList<RefBatch> AllBatches => _allBatches;

    public Replica this[int index] => _replicas[index];

    /// <summary>Locally commit a transaction on one replica and enqueue the batch for delivery.</summary>
    public RefBatch Commit(int replicaIndex, Action<LocalWriter.TransactionBuilder> build)
    {
        Replica replica = _replicas[replicaIndex];
        RefBatch batch = replica.Writer.Transact(build);
        ApplyResult result = replica.Document.Apply(batch);
        if (result.Status is not ApplyStatus.Integrated and not ApplyStatus.Duplicate)
        {
            throw new InvalidOperationException($"Local commit failed: {result.Status} {result.Detail}");
        }

        _allBatches.Add(batch);
        return batch;
    }

    /// <summary>
    /// Deliver every recorded batch to every replica under a deterministic shuffled order,
    /// optionally duplicating deliveries.
    /// </summary>
    public void DeliverAll(bool duplicate = true)
    {
        var deliveries = new List<(int Replica, RefBatch Batch)>();
        foreach (RefBatch batch in _allBatches)
        {
            for (int r = 0; r < _replicas.Count; r++)
            {
                deliveries.Add((r, batch));
                if (duplicate && _random.Next(0, 3) == 0)
                {
                    deliveries.Add((r, batch));
                }
            }
        }

        Shuffle(deliveries);

        foreach ((int replicaIndex, RefBatch batch) in deliveries)
        {
            _ = _replicas[replicaIndex].Document.Apply(batch);
        }

        // Drain pending by replaying in several passes (dependencies may arrive later in shuffle).
        for (int pass = 0; pass < _allBatches.Count + 2; pass++)
        {
            foreach (Replica replica in _replicas)
            {
                foreach (RefBatch batch in _allBatches)
                {
                    _ = replica.Document.Apply(batch);
                }
            }
        }
    }

    /// <summary>Assert all replicas share the same visible fingerprint.</summary>
    public void AssertConverged()
    {
        string expected = _replicas[0].Document.VisibleFingerprint();
        for (int i = 1; i < _replicas.Count; i++)
        {
            string actual = _replicas[i].Document.VisibleFingerprint();
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Replica {i} diverged for seed {Seed}.{Environment.NewLine}Expected: {expected}{Environment.NewLine}Actual:   {actual}");
            }
        }
    }

    public string Fingerprint() => _replicas[0].Document.VisibleFingerprint();

    private void Shuffle<T>(IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    public sealed class Replica
    {
        public Replica(SessionId session, ReferenceDocument document, LocalWriter writer)
        {
            Session = session;
            Document = document;
            Writer = writer;
        }

        public SessionId Session { get; }

        public ReferenceDocument Document { get; }

        public LocalWriter Writer { get; }
    }
}
