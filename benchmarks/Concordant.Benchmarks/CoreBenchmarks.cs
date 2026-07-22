using BenchmarkDotNet.Attributes;
using Concordant.Shared;
using Concordant.Values;

namespace Concordant.Benchmarks;

/// <summary>Local edit latency on fixed small/medium visible documents.</summary>
[Config(typeof(FastInProcessConfig))]
[MemoryDiagnoser]
public class LocalEditBenchmarks
{
    private ConcordantDocument _small = null!;
    private ConcordantDocument _medium = null!;
    private int _smallLen;
    private int _mediumLen;

    [GlobalSetup]
    public void Setup()
    {
        _small = WorkloadFactory.CreateVisibleText(1, WorkloadFactory.SmallVisibleChars);
        _medium = WorkloadFactory.CreateVisibleText(2, WorkloadFactory.MediumVisibleChars);
        _smallLen = _small.GetText("notes").Length;
        _mediumLen = _medium.GetText("notes").Length;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _small.Dispose();
        _medium.Dispose();
    }

    [Benchmark(Description = "LocalEdit_Small")]
    public int LocalEdit_Small()
    {
        _ = _small.Transact(tx =>
        {
            SharedText text = tx.Text("notes");
            text.Insert(_smallLen, "Z");
            text.Delete(_smallLen, 1);
        });
        return _smallLen;
    }

    [Benchmark(Description = "LocalEdit_Medium")]
    public int LocalEdit_Medium()
    {
        _ = _medium.Transact(tx =>
        {
            SharedText text = tx.Text("notes");
            text.Insert(_mediumLen, "Z");
            text.Delete(_mediumLen, 1);
        });
        return _mediumLen;
    }
}

/// <summary>Remote update apply for normal small/medium batches.</summary>
[Config(typeof(FastInProcessConfig))]
[MemoryDiagnoser]
public class ApplyUpdateBenchmarks
{
    private byte[] _smallUpdate = null!;
    private byte[] _mediumUpdate = null!;
    private ulong _seed;

    [GlobalSetup]
    public void Setup()
    {
        using ConcordantDocument smallSrc = WorkloadFactory.CreateVisibleText(11, WorkloadFactory.SmallVisibleChars);
        using ConcordantDocument mediumSrc = WorkloadFactory.CreateVisibleText(12, WorkloadFactory.MediumVisibleChars);
        _ = smallSrc.Transact(tx => tx.GetOrCreateMap("meta").Set("n", ConcordantScalar.Int64(1)));
        _ = mediumSrc.Transact(tx => tx.GetOrCreateMap("meta").Set("n", ConcordantScalar.Int64(2)));
        _smallUpdate = smallSrc.EncodeUpdateSince(new Dictionary<SessionId, ulong>());
        _mediumUpdate = mediumSrc.EncodeUpdateSince(new Dictionary<SessionId, ulong>());
        _seed = 100;
    }

    [Benchmark(Description = "ApplyUpdate_Small")]
    public ApplyStatus ApplyUpdate_Small()
    {
        using ConcordantDocument target = WorkloadFactory.CreateEmpty(_seed++);
        return target.ApplyUpdate(_smallUpdate).Status;
    }

    [Benchmark(Description = "ApplyUpdate_Medium")]
    public ApplyStatus ApplyUpdate_Medium()
    {
        using ConcordantDocument target = WorkloadFactory.CreateEmpty(_seed++);
        return target.ApplyUpdate(_mediumUpdate).Status;
    }
}

/// <summary>Checkpoint encode/load for medium documents.</summary>
[Config(typeof(FastInProcessConfig))]
[MemoryDiagnoser]
public class SyncBenchmarks
{
    private byte[] _checkpoint = null!;

    [GlobalSetup]
    public void Setup()
    {
        using ConcordantDocument src = WorkloadFactory.CreateVisibleText(21, WorkloadFactory.MediumVisibleChars);
        _checkpoint = src.EncodeFullState();
    }

    [Benchmark(Description = "CheckpointLoad_Medium")]
    public string CheckpointLoad_Medium()
    {
        using ConcordantDocument loaded = ConcordantDocument.CreateFromCheckpoint(_checkpoint);
        return loaded.VisibleFingerprint();
    }
}

/// <summary>Metadata growth and encode cost for medium documents and session churn.</summary>
[Config(typeof(FastInProcessConfig))]
[MemoryDiagnoser]
public class MetadataBenchmarks
{
    private ConcordantDocument _medium = null!;
    private ConcordantDocument _churn = null!;

    [GlobalSetup]
    public void Setup()
    {
        _medium = WorkloadFactory.CreateVisibleText(31, WorkloadFactory.MediumVisibleChars);
        _churn = WorkloadFactory.CreateHistoricalSessionChurn(32, sessions: 500);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _medium.Dispose();
        _churn.Dispose();
    }

    [Benchmark(Description = "EncodeFullState_Medium")]
    public int EncodeFullState_Medium() => _medium.EncodeFullState().Length;

    [Benchmark(Description = "EncodeUpdateSince_EmptyRemote_Medium")]
    public int EncodeUpdateSince_EmptyRemote_Medium() =>
        _medium.EncodeUpdateSince(new Dictionary<SessionId, ulong>()).Length;

    [Benchmark(Description = "StateVectorSessions_Churn500")]
    public long StateVectorSessions_Churn500() => WorkloadFactory.StateVectorSessionCount(_churn);
}

/// <summary>Isolated sequential append, random edit, pending integration, and rollback workloads.</summary>
[Config(typeof(FastInProcessConfig))]
[MemoryDiagnoser]
public class ScalingPathBenchmarks
{
    private ConcordantDocument _rollbackDoc = null!;
    private byte[] _gapUpdate = null!;
    private byte[] _prefixUpdate = null!;
    private byte[] _checkpoint = null!;
    private ulong _seed;

    [GlobalSetup]
    public void Setup()
    {
        _rollbackDoc = WorkloadFactory.CreateVisibleText(43, WorkloadFactory.SmallVisibleChars);
        (_, _gapUpdate, _prefixUpdate) = WorkloadFactory.CreatePendingIntegrationPair(
            44,
            WorkloadFactory.PendingFillerOps);
        using ConcordantDocument src = WorkloadFactory.CreateVisibleText(45, WorkloadFactory.MediumVisibleChars);
        _checkpoint = src.EncodeFullState();
        _seed = 200;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _rollbackDoc.Dispose();
    }

    [Benchmark(Description = "SequentialInsert_4k")]
    public long SequentialInsert_4k()
    {
        using ConcordantDocument doc = WorkloadFactory.CreateSequentialInserts(
            _seed++,
            WorkloadFactory.SequentialInsertCount);
        return WorkloadFactory.IntegratedOpEstimate(doc);
    }

    [Benchmark(Description = "RandomInsertDelete_2k")]
    public long RandomInsertDelete_2k()
    {
        using ConcordantDocument doc = WorkloadFactory.CreateRandomInsertDelete(
            _seed++,
            WorkloadFactory.RandomEditOps);
        return WorkloadFactory.IntegratedOpEstimate(doc);
    }

    [Benchmark(Description = "PendingIntegration_ApplyGapThenPrefix")]
    public ApplyStatus PendingIntegration_ApplyGapThenPrefix()
    {
        using ConcordantDocument target = WorkloadFactory.CreateEmpty(_seed++);
        ApplyResult gap = target.ApplyUpdate(_gapUpdate);
        if (gap.Status is ApplyStatus.Rejected)
        {
            throw new InvalidOperationException(gap.Detail ?? "Gap apply rejected.");
        }

        return target.ApplyUpdate(_prefixUpdate).Status;
    }

    [Benchmark(Description = "CheckpointLoad_Medium_Isolated")]
    public string CheckpointLoad_Medium_Isolated()
    {
        using ConcordantDocument loaded = ConcordantDocument.CreateFromCheckpoint(_checkpoint);
        return loaded.VisibleFingerprint();
    }

    [Benchmark(Description = "TransactionRollback_Small")]
    public int TransactionRollback_Small()
    {
        WorkloadFactory.RunTransactionRollback(_rollbackDoc);
        return _rollbackDoc.GetText("notes").Length;
    }
}
