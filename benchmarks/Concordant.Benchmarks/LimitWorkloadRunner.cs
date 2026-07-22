using System.Diagnostics;

namespace Concordant.Benchmarks;

/// <summary>
/// One-shot limit workloads (fragmented history, 100-replica reconcile, session churn, scaling probes)
/// that are too heavy for repeated BenchmarkDotNet iterations.
/// </summary>
internal static class LimitWorkloadRunner
{
    public static int Run(bool smoke = false)
    {
        Console.WriteLine("=== Concordant limit workloads (one-shot) ===");
        Console.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        Console.WriteLine($"Arch: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine(smoke ? "Mode: smoke (reduced fragmented ops)" : "Mode: full gate");
        Console.WriteLine();

        int fragmentedOps = smoke ? WorkloadFactory.SmokeFragmentedOps : WorkloadFactory.LimitFragmentedOps;
        RunFragmentedHistory(fragmentedOps);
        RunSequentialInsert();
        RunRandomInsertDelete();
        RunPendingIntegration();
        RunTransactionRollback();
        RunCheckpointLoad();
        RunActiveReplicaReconcile();
        RunHistoricalSessionChurn();
        return 0;
    }

    private static void RunFragmentedHistory(int targetOps)
    {
        Console.WriteLine(
            $"[FragmentedHistory] targetOps={targetOps} (plan target {WorkloadFactory.LimitFragmentedOpsPlanTarget}; beta.2 gate={WorkloadFactory.LimitFragmentedOps}, smoke={WorkloadFactory.SmokeFragmentedOps})");
        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        using ConcordantDocument doc = WorkloadFactory.CreateFragmentedHistory(77, targetOps);
        sw.Stop();
        long afterAlloc = GC.GetAllocatedBytesForCurrentThread();

        long ops = WorkloadFactory.IntegratedOpEstimate(doc);
        int visible = doc.GetText("notes").Length;
        long encodeSwStart = Stopwatch.GetTimestamp();
        byte[] checkpoint = doc.EncodeFullState();
        double encodeMs = Stopwatch.GetElapsedTime(encodeSwStart).TotalMilliseconds;

        Console.WriteLine($"  build_ms={sw.Elapsed.TotalMilliseconds:F1}");
        Console.WriteLine($"  integrated_ops≈{ops}");
        Console.WriteLine($"  visible_chars={visible}");
        Console.WriteLine($"  checkpoint_bytes={checkpoint.Length}");
        Console.WriteLine($"  encode_fullstate_ms={encodeMs:F1}");
        Console.WriteLine($"  alloc_bytes≈{afterAlloc - beforeAlloc}");
        Console.WriteLine($"  sessions={WorkloadFactory.StateVectorSessionCount(doc)}");
        Console.WriteLine();
    }

    private static void RunSequentialInsert()
    {
        Console.WriteLine($"[SequentialInsert] count={WorkloadFactory.SequentialInsertCount}");
        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        using ConcordantDocument doc = WorkloadFactory.CreateSequentialInserts(
            91,
            WorkloadFactory.SequentialInsertCount);
        sw.Stop();
        long afterAlloc = GC.GetAllocatedBytesForCurrentThread();
        Console.WriteLine($"  build_ms={sw.Elapsed.TotalMilliseconds:F1}");
        Console.WriteLine($"  integrated_ops≈{WorkloadFactory.IntegratedOpEstimate(doc)}");
        Console.WriteLine($"  alloc_bytes≈{afterAlloc - beforeAlloc}");
        Console.WriteLine();
    }

    private static void RunRandomInsertDelete()
    {
        Console.WriteLine($"[RandomInsertDelete] ops={WorkloadFactory.RandomEditOps}");
        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        using ConcordantDocument doc = WorkloadFactory.CreateRandomInsertDelete(
            92,
            WorkloadFactory.RandomEditOps);
        sw.Stop();
        long afterAlloc = GC.GetAllocatedBytesForCurrentThread();
        Console.WriteLine($"  build_ms={sw.Elapsed.TotalMilliseconds:F1}");
        Console.WriteLine($"  integrated_ops≈{WorkloadFactory.IntegratedOpEstimate(doc)}");
        Console.WriteLine($"  visible_chars={doc.GetText("notes").Length}");
        Console.WriteLine($"  alloc_bytes≈{afterAlloc - beforeAlloc}");
        Console.WriteLine();
    }

    private static void RunPendingIntegration()
    {
        Console.WriteLine($"[PendingIntegration] fillerOps={WorkloadFactory.PendingFillerOps}");
        (ConcordantDocument target, byte[] gapUpdate, byte[] prefixUpdate) =
            WorkloadFactory.CreatePendingIntegrationPair(93, WorkloadFactory.PendingFillerOps);
        using (target)
        {
            long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            ApplyResult gap = target.ApplyUpdate(gapUpdate);
            ApplyResult prefix = target.ApplyUpdate(prefixUpdate);
            sw.Stop();
            long afterAlloc = GC.GetAllocatedBytesForCurrentThread();

            if (gap.Status is ApplyStatus.Rejected || prefix.Status is ApplyStatus.Rejected)
            {
                throw new InvalidOperationException(
                    $"Pending integration failed: gap={gap.Status}/{gap.Detail}; prefix={prefix.Status}/{prefix.Detail}");
            }

            Console.WriteLine($"  apply_ms={sw.Elapsed.TotalMilliseconds:F1}");
            Console.WriteLine($"  gap_status={gap.Status}");
            Console.WriteLine($"  prefix_status={prefix.Status}");
            Console.WriteLine($"  pending_after={target.PendingOperationCount}");
            Console.WriteLine($"  integrated_ops≈{WorkloadFactory.IntegratedOpEstimate(target)}");
            Console.WriteLine($"  alloc_bytes≈{afterAlloc - beforeAlloc}");
            Console.WriteLine();
        }
    }

    private static void RunTransactionRollback()
    {
        Console.WriteLine("[TransactionRollback] small visible + failed Transact");
        using ConcordantDocument doc = WorkloadFactory.CreateVisibleText(94, WorkloadFactory.SmallVisibleChars);
        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
        {
            WorkloadFactory.RunTransactionRollback(doc);
        }

        sw.Stop();
        long afterAlloc = GC.GetAllocatedBytesForCurrentThread();
        Console.WriteLine($"  rollback_x100_ms={sw.Elapsed.TotalMilliseconds:F1}");
        Console.WriteLine($"  alloc_bytes≈{afterAlloc - beforeAlloc}");
        Console.WriteLine();
    }

    private static void RunCheckpointLoad()
    {
        Console.WriteLine("[CheckpointLoad] medium full-state");
        using ConcordantDocument src = WorkloadFactory.CreateVisibleText(95, WorkloadFactory.MediumVisibleChars);
        byte[] checkpoint = src.EncodeFullState();
        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        using ConcordantDocument loaded = ConcordantDocument.CreateFromCheckpoint(checkpoint);
        string fingerprint = loaded.VisibleFingerprint();
        sw.Stop();
        long afterAlloc = GC.GetAllocatedBytesForCurrentThread();
        Console.WriteLine($"  load_ms={sw.Elapsed.TotalMilliseconds:F1}");
        Console.WriteLine($"  checkpoint_bytes={checkpoint.Length}");
        Console.WriteLine($"  fingerprint_len={fingerprint.Length}");
        Console.WriteLine($"  alloc_bytes≈{afterAlloc - beforeAlloc}");
        Console.WriteLine();
    }

    private static void RunActiveReplicaReconcile()
    {
        Console.WriteLine($"[ActiveReplicas] count={WorkloadFactory.ActiveReplicaCount}");
        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        (ConcordantDocument[] replicas, byte[][] updates) = WorkloadFactory.CreateActiveReplicas(WorkloadFactory.ActiveReplicaCount);
        WorkloadFactory.ReconcileAll(replicas, updates);
        sw.Stop();
        long afterAlloc = GC.GetAllocatedBytesForCurrentThread();

        string fingerprint = replicas[0].VisibleFingerprint();
        for (int i = 1; i < replicas.Length; i++)
        {
            if (!string.Equals(fingerprint, replicas[i].VisibleFingerprint(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Limit reconcile diverged.");
            }
        }

        Console.WriteLine($"  reconcile_ms={sw.Elapsed.TotalMilliseconds:F1}");
        Console.WriteLine($"  alloc_bytes≈{afterAlloc - beforeAlloc}");
        Console.WriteLine($"  fingerprint_len={fingerprint.Length}");
        Console.WriteLine($"  fingerprint_hash={fingerprint.GetHashCode():X8}");
        Console.WriteLine();

        foreach (ConcordantDocument replica in replicas)
        {
            replica.Dispose();
        }
    }

    private static void RunHistoricalSessionChurn()
    {
        Console.WriteLine($"[HistoricalSessionChurn] sessions={WorkloadFactory.HistoricalSessionChurn}");
        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        using ConcordantDocument doc = WorkloadFactory.CreateHistoricalSessionChurn(
            88,
            WorkloadFactory.HistoricalSessionChurn);
        sw.Stop();
        long afterAlloc = GC.GetAllocatedBytesForCurrentThread();

        byte[] checkpoint = doc.EncodeFullState();
        Console.WriteLine($"  build_ms={sw.Elapsed.TotalMilliseconds:F1}");
        Console.WriteLine($"  sessions={WorkloadFactory.StateVectorSessionCount(doc)}");
        Console.WriteLine($"  integrated_ops≈{WorkloadFactory.IntegratedOpEstimate(doc)}");
        Console.WriteLine($"  checkpoint_bytes={checkpoint.Length}");
        Console.WriteLine($"  alloc_bytes≈{afterAlloc - beforeAlloc}");
        Console.WriteLine();
    }
}
