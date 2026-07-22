using Concordant.Shared;
using Concordant.Values;

namespace Concordant.Benchmarks;

/// <summary>Deterministic document builders for fixed small/medium/limit workloads.</summary>
internal static class WorkloadFactory
{
    public const int SmallVisibleChars = 128;
    /// <summary>Medium visible size for BDN (~2k ops). Remote apply of this size is the documented beta.2 budget workload.</summary>
    public const int MediumVisibleChars = 2_048;
    /// <summary>
    /// Executable fragmented-history gate for beta.2. Full 1M remains a stretch goal — see
    /// <see cref="LimitFragmentedOpsPlanTarget"/> and docs/benchmarks for the path to 1M.
    /// </summary>
    public const int LimitFragmentedOps = 100_000;
    public const int LimitFragmentedOpsPlanTarget = 1_000_000;
    /// <summary>CI/manual smoke proxy that finishes in seconds on the reference laptop.</summary>
    public const int SmokeFragmentedOps = 10_000;
    public const int ActiveReplicaCount = 100;
    public const int HistoricalSessionChurn = 2_000;
    public const int SequentialInsertCount = 4_096;
    public const int RandomEditOps = 2_048;
    public const int PendingFillerOps = 64;

    public static ConcordantDocument CreateEmpty(ulong seed) =>
        new(new ConcordantDocumentOptions
        {
            WriterSession = SessionId.FromSeed(seed),
            MaxOperations = 5_000_000,
            MaxHistoricalSessions = 500_000,
            MaxUpdateBytes = 256L * 1024 * 1024,
        });

    /// <summary>Builds a text root with <paramref name="visibleChars"/> visible characters (one op per char + root).</summary>
    public static ConcordantDocument CreateVisibleText(ulong seed, int visibleChars)
    {
        ConcordantDocument doc = CreateEmpty(seed);
        _ = doc.Transact(tx =>
        {
            SharedText text = tx.GetOrCreateText("notes");
            text.Insert(0, new string('a', visibleChars));
        });
        return doc;
    }

    /// <summary>
    /// Builds fragmented history: insert then delete every other character so tombstones remain.
    /// Yields roughly <paramref name="targetOps"/> integrated operations (≈ inserts + deletes + root).
    /// </summary>
    public static ConcordantDocument CreateFragmentedHistory(ulong seed, int targetOps)
    {
        ConcordantDocument doc = CreateEmpty(seed);
        // Root declare + inserts + deletes ≈ 1 + n + n/2 when deleting every other char.
        int inserts = Math.Max(2, (targetOps * 2) / 3);
        const int chunk = 1_024;
        for (int written = 0; written < inserts;)
        {
            int take = Math.Min(chunk, inserts - written);
            _ = doc.Transact(tx =>
            {
                SharedText text = tx.GetOrCreateText("notes");
                text.Insert(text.Length, new string('x', take));
            });
            written += take;
        }

        // One transaction: delete every other visible character from the end (retains tombstones).
        _ = doc.Transact(tx =>
        {
            SharedText text = tx.Text("notes");
            for (int i = text.Length - 1; i >= 0; i -= 2)
            {
                text.Delete(i, 1);
            }
        });

        return doc;
    }

    /// <summary>Append-only sequential inserts (end of text) — isolates end-append integration cost.</summary>
    public static ConcordantDocument CreateSequentialInserts(ulong seed, int insertCount)
    {
        ConcordantDocument doc = CreateEmpty(seed);
        const int chunk = 256;
        for (int written = 0; written < insertCount;)
        {
            int take = Math.Min(chunk, insertCount - written);
            _ = doc.Transact(tx =>
            {
                SharedText text = tx.GetOrCreateText("notes");
                text.Insert(text.Length, new string('s', take));
            });
            written += take;
        }

        return doc;
    }

    /// <summary>Deterministic random insert/delete mix against a growing text buffer.</summary>
    public static ConcordantDocument CreateRandomInsertDelete(ulong seed, int ops)
    {
        ConcordantDocument doc = CreateEmpty(seed);
        var rng = new Random(unchecked((int)seed));
        _ = doc.Transact(tx => tx.GetOrCreateText("notes").Insert(0, "seed"));

        for (int i = 0; i < ops; i++)
        {
            _ = doc.Transact(tx =>
            {
                SharedText text = tx.Text("notes");
                if (text.Length == 0 || rng.NextDouble() < 0.6)
                {
                    int at = rng.Next(0, text.Length + 1);
                    text.Insert(at, ((char)('a' + (i % 26))).ToString());
                }
                else
                {
                    int at = rng.Next(0, text.Length);
                    text.Delete(at, 1);
                }
            });
        }

        return doc;
    }

    /// <summary>
    /// Isolated pending-integration pair: <paramref name="GapUpdate"/> arrives first (B depends on A)
    /// and stays pending until <paramref name="PrefixUpdate"/> (A) is applied.
    /// </summary>
    public static (ConcordantDocument Target, byte[] GapUpdate, byte[] PrefixUpdate) CreatePendingIntegrationPair(
        ulong seed,
        int fillerOps)
    {
        using ConcordantDocument sessionA = CreateEmpty(seed + 10);
        _ = sessionA.Transact(tx =>
        {
            SharedText text = tx.GetOrCreateText("notes");
            text.Insert(0, "A");
            if (fillerOps > 1)
            {
                text.Insert(1, new string('a', Math.Min(fillerOps - 1, 64)));
            }
        });
        byte[] prefixUpdate = sessionA.EncodeUpdateSince(new Dictionary<SessionId, ulong>());

        // B recovers A's visible state with a fresh writer session, then appends.
        using ConcordantDocument sessionB = ConcordantDocument.CreateFromCheckpoint(sessionA.EncodeFullState());
        _ = sessionB.Transact(tx =>
        {
            SharedText text = tx.Text("notes");
            text.Insert(text.Length, "B");
        });
        byte[] gapUpdate = sessionB.EncodeUpdateSince(sessionA.StateVector);

        ConcordantDocument target = CreateEmpty(seed);
        return (target, gapUpdate, prefixUpdate);
    }

    /// <summary>Creates <paramref name="replicaCount"/> replicas that each own a small disjoint edit, then returns encoded updates.</summary>
    public static (ConcordantDocument[] Replicas, byte[][] Updates) CreateActiveReplicas(int replicaCount)
    {
        var replicas = new ConcordantDocument[replicaCount];
        var updates = new byte[replicaCount][];
        for (int i = 0; i < replicaCount; i++)
        {
            replicas[i] = CreateEmpty(10_000UL + (ulong)i);
            _ = replicas[i].Transact(tx =>
            {
                SharedMap map = tx.GetOrCreateMap("meta");
                map.Set($"r{i}", ConcordantScalar.Int64(i));
                SharedText text = tx.GetOrCreateText("notes");
                text.Insert(0, $"r{i};");
            });
            updates[i] = replicas[i].EncodeUpdateSince(new Dictionary<SessionId, ulong>());
        }

        return (replicas, updates);
    }

    /// <summary>Applies every replica update to every other replica (full mesh reconcile).</summary>
    public static void ReconcileAll(ConcordantDocument[] replicas, byte[][] updates)
    {
        for (int i = 0; i < replicas.Length; i++)
        {
            for (int j = 0; j < updates.Length; j++)
            {
                if (i == j)
                {
                    continue;
                }

                ApplyResult result = replicas[i].ApplyUpdate(updates[j]);
                if (result.Status is ApplyStatus.Rejected)
                {
                    throw new InvalidOperationException(result.Detail ?? "Reconcile rejected.");
                }
            }
        }
    }

    /// <summary>Creates many historical sessions with one root-declare+map-set each (session churn).</summary>
    public static ConcordantDocument CreateHistoricalSessionChurn(ulong seed, int sessions)
    {
        ConcordantDocument sink = CreateEmpty(seed);
        for (int i = 0; i < sessions; i++)
        {
            using ConcordantDocument remote = CreateEmpty(50_000UL + (ulong)i);
            _ = remote.Transact(tx =>
            {
                SharedMap map = tx.GetOrCreateMap("churn");
                map.Set("k", ConcordantScalar.Int64(i));
            });
            byte[] update = remote.EncodeUpdateSince(new Dictionary<SessionId, ulong>());
            ApplyResult applied = sink.ApplyUpdate(update);
            if (applied.Status is ApplyStatus.Rejected)
            {
                throw new InvalidOperationException(applied.Detail ?? "Churn apply rejected.");
            }
        }

        return sink;
    }

    /// <summary>
    /// Runs a transaction that mutates then throws — documents must remain unchanged (atomic rollback).
    /// </summary>
    public static void RunTransactionRollback(ConcordantDocument doc)
    {
        string before = doc.VisibleFingerprint();
        long opsBefore = IntegratedOpEstimate(doc);
        try
        {
            _ = doc.Transact(tx =>
            {
                SharedText text = tx.GetOrCreateText("notes");
                text.Insert(text.Length, "ROLLBACK");
                throw new InvalidOperationException("benchmark rollback");
            });
        }
        catch (InvalidOperationException ex) when (ex.Message == "benchmark rollback")
        {
            // expected
        }

        if (!string.Equals(before, doc.VisibleFingerprint(), StringComparison.Ordinal)
            || opsBefore != IntegratedOpEstimate(doc))
        {
            throw new InvalidOperationException("Transaction rollback leaked state.");
        }
    }

    public static long IntegratedOpEstimate(ConcordantDocument doc) =>
        doc.StateVector.Values.Aggregate(0L, static (sum, clock) => sum + (long)clock);

    public static long StateVectorSessionCount(ConcordantDocument doc) => doc.StateVector.Count;
}
