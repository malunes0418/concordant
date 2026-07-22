# Benchmarks

Fixed workloads for `0.1.0-beta.2` baselines. Prefer documenting results over last-minute micro-optimizations unless a hot path is clearly broken **and** oracle/wire traces remain identical.

## Workloads

| Name | Size | What it measures |
|---|---|---|
| `LocalEdit_Small` / `LocalEdit_Medium` | 128 / 2,048 visible chars | Local transaction insert+delete latency & allocations |
| `ApplyUpdate_Small` / `ApplyUpdate_Medium` | same | Remote batch decode+merge |
| `CheckpointLoad_Medium` | medium full-state | `CreateFromCheckpoint` |
| `EncodeFullState_Medium` / `EncodeUpdateSince_*` | medium | Encode cost & payload size |
| `StateVectorSessions_Churn500` | 500 sessions | State-vector / metadata cardinality |
| `SequentialInsert_4k` | 4,096 end-appends | Sequential insert integration |
| `RandomInsertDelete_2k` | 2,048 mixed edits | Middle insert/delete + rank shifts |
| `PendingIntegration_ApplyGapThenPrefix` | cross-session gap | Isolated pending → integrate path |
| `TransactionRollback_Small` | failed `Transact` | Atomic rollback correctness/cost |
| `--limit` / `--limit-smoke` FragmentedHistory | **100k** gate / **10k** smoke (plan target 1M) | Fragmented insert/delete history |
| `--limit` ActiveReplicas | 100 | One-shot full-mesh reconcile |
| `--limit` HistoricalSessionChurn | 2,000 sessions | Session churn build cost & checkpoint growth |

Constants live in `benchmarks/Concordant.Benchmarks/WorkloadFactory.cs`. BDN uses an in-process job (`FastInProcessConfig`) so release gates finish without per-benchmark process spawn.

## How to run

```bash
# BenchmarkDotNet suite (both TFMs)
dotnet run --project benchmarks/Concordant.Benchmarks --framework net8.0 --configuration Release -- --filter "*"
dotnet run --project benchmarks/Concordant.Benchmarks --framework net10.0 --configuration Release -- --filter "*"

# One-shot limit workloads (100k fragmented gate + scaling probes)
dotnet run --project benchmarks/Concordant.Benchmarks --framework net8.0 --configuration Release -- --limit
dotnet run --project benchmarks/Concordant.Benchmarks --framework net10.0 --configuration Release -- --limit

# Fast smoke (10k fragmented + same probes; suitable for manual/CI smoke)
dotnet run --project benchmarks/Concordant.Benchmarks --framework net8.0 --configuration Release -- --limit-smoke
```

## Targets (reference machine)

| Class | Budget | Status (beta.2) |
|---|---|---|
| Local edits | p95 &lt; 1 ms | Met (small + medium) |
| Small remote batch (~128 ops) | p95 &lt; 16 ms | Met |
| Medium remote batch (~2k ops) | p95 &lt; 100 ms (net10); document net8 | **Re-baselined** from aspirational 16 ms (beta.1 was multi-second) |
| Fragmented history | **100k ops** executable with recorded time/alloc; 1M = stretch | Gate met; 1M still out of window |

### Path to 1M fragmented history

Remaining hotspots (not blocking beta.2):

1. **UTF-16 visible `List` middle insert/delete** — still O(n) array shifts; needs a rope/fenwick/order-statistic tree for the visible rank index.
2. **Apply-time `CaptureSnapshot`** — full store clone near retention-quota safety; skip or CoW when far from quotas.
3. **Per-op integrate overhead** — decode + pending index + YATA conflict walk (~30–40 µs/op on the reference laptop).

Until those land, keep `--limit` at 100k and `--limit-smoke` at 10k.

## Results

- [net8.0 results](results-net8.md)
- [net10.0 results](results-net10.md)
