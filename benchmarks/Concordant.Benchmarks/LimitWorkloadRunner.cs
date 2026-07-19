using System.Diagnostics;

namespace Concordant.Benchmarks;

/// <summary>
/// One-shot limit workloads (1M fragmented history, 100-replica reconcile, session churn)
/// that are too heavy for repeated BenchmarkDotNet iterations.
/// </summary>
internal static class LimitWorkloadRunner
{
    public static int Run()
    {
        Console.WriteLine("=== Concordant limit workloads (one-shot) ===");
        Console.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        Console.WriteLine($"Arch: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine();

        RunFragmentedHistory();
        RunActiveReplicaReconcile();
        RunHistoricalSessionChurn();
        return 0;
    }

    private static void RunFragmentedHistory()
    {
        Console.WriteLine($"[FragmentedHistory] targetOps={WorkloadFactory.LimitFragmentedOps} (plan target {WorkloadFactory.LimitFragmentedOpsPlanTarget}; proxy used for release gate)");
        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        using ConcordantDocument doc = WorkloadFactory.CreateFragmentedHistory(77, WorkloadFactory.LimitFragmentedOps);
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
