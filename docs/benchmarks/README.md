# Benchmarks

Fixed workloads for `0.1.0-beta.1` baselines. Prefer documenting results over last-minute micro-optimizations unless a hot path is clearly broken **and** oracle/wire traces remain identical.

## Workloads

| Name | Size | What it measures |
|---|---|---|
| `LocalEdit_Small` / `LocalEdit_Medium` | 128 / 2,048 visible chars | Local transaction insert+delete latency & allocations |
| `ApplyUpdate_Small` / `ApplyUpdate_Medium` | same | Remote batch decode+merge |
| `CheckpointLoad_Medium` | medium full-state | `CreateFromCheckpoint` |
| `EncodeFullState_Medium` / `EncodeUpdateSince_*` | medium | Encode cost & payload size |
| `StateVectorSessions_Churn500` | 500 sessions | State-vector / metadata cardinality |
| `--limit` FragmentedHistory | **10k ops** (plan target 1M) | Fragmented insert/delete history, checkpoint size, allocs |
| `--limit` ActiveReplicas | 100 | One-shot full-mesh reconcile |
| `--limit` HistoricalSessionChurn | 2,000 sessions | Session churn build cost & checkpoint growth |

Constants live in `benchmarks/Concordant.Benchmarks/WorkloadFactory.cs`. BDN uses an in-process job (`FastInProcessConfig`) so release gates finish without per-benchmark process spawn.

## How to run

```bash
# BenchmarkDotNet suite (both TFMs)
dotnet run --project benchmarks/Concordant.Benchmarks --framework net8.0 --configuration Release -- --filter "*"
dotnet run --project benchmarks/Concordant.Benchmarks --framework net10.0 --configuration Release -- --filter "*"

# One-shot limit workloads
dotnet run --project benchmarks/Concordant.Benchmarks --framework net8.0 --configuration Release -- --limit
dotnet run --project benchmarks/Concordant.Benchmarks --framework net10.0 --configuration Release -- --limit
```

## Targets (reference machine)

Plan targets (aspirational):

- Local edits: **p95 &lt; 1 ms**
- Normal remote batches: **p95 &lt; 16 ms**

On the 2026-07-19 reference laptop, **local edits and small applies meet those targets**; **medium (~2k-op) apply/checkpoint do not** (multi-second). Documented as a known scaling gap — not micro-optimized in this beta.

## Results

- [net8.0 results](results-net8.md)
- [net10.0 results](results-net10.md)
