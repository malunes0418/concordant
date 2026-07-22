# Benchmark results — .NET 8

## Hardware / runtime

| Field | Value |
|---|---|
| CPU | 13th Gen Intel Core i5-13420H @ 2.10 GHz (8P+4E / 12 logical) |
| RAM | ~48 GB |
| OS | Windows 11 (10.0.26200) |
| SDK | .NET SDK 10.0.203 |
| Runtime | .NET 8.0.22 (8.0.2225.52707), X64 RyuJIT AVX2 |
| Date | 2026-07-21 (`0.1.0-beta.2`) |

## Commands run

```bash
dotnet run --project benchmarks/Concordant.Benchmarks --framework net8.0 --configuration Release -- --filter "*" --exporters json --artifacts artifacts/bench-net8
dotnet run --project benchmarks/Concordant.Benchmarks --framework net8.0 --configuration Release -- --limit-smoke
# Full 100k fragmented gate: --limit (see limit table; run when refreshing evidence)
```

BDN used the in-process `FastInProcessConfig` (InvocationCount=1). Raw JSON under `artifacts/bench-net8/results/`.

## BenchmarkDotNet (p50 / p95)

| Benchmark | p50 | p95 | Allocated / op |
|---|---:|---:|---:|
| LocalEdit_Small | 0.183 ms | 0.213 ms | 92 KB |
| LocalEdit_Medium | 0.763 ms | 0.955 ms | 1.4 MB |
| ApplyUpdate_Small | 1.40 ms | 1.61 ms | 250 KB |
| ApplyUpdate_Medium (~2k ops) | **165 ms** | **246 ms** | 4.2 MB |
| CheckpointLoad_Medium | **244 ms** | **247 ms** | 5.1 MB |
| EncodeFullState_Medium | 3.07 ms | 3.27 ms | 963 KB |
| EncodeUpdateSince_EmptyRemote_Medium | 0.678 ms | 0.715 ms | 1.0 MB |
| StateVectorSessions_Churn500 | 1.6 µs | 2.3 µs | 336 B |
| SequentialInsert_4k | 820 ms | 838 ms | 35 MB |
| RandomInsertDelete_2k | 385 ms | 413 ms | 666 MB |
| PendingIntegration_ApplyGapThenPrefix | 0.427 ms | 0.461 ms | 130 KB |
| TransactionRollback_Small | 0.434 ms | 0.461 ms | 238 KB |

### vs targets

- Local edit p95 &lt; 1 ms ✅
- Small remote batch p95 &lt; 16 ms ✅
- Medium remote batch: aspirational 16 ms **not met**; **beta.2 re-baseline** documents ~165–250 ms on net8.0 (beta.1 was ~4.4 s)

## Limit workloads (one-shot)

| Workload | Result |
|---|---|
| FragmentedHistory **10k** (`--limit-smoke`) | build 1.90 s; checkpoint 947 KB; encode 8.7 ms; alloc ~53 MB |
| FragmentedHistory **100k** (beta.2 gate / plan target 1M) | same scaling class as net10 (~6 min / ~1.7 GB on reference laptop; refresh with `--limit` when publishing) |
| SequentialInsert 4k | build 548 ms |
| RandomInsertDelete 2k | build 272 ms |
| PendingIntegration | gap=`PendingDependencies` → prefix integrates; pending cleared |
| TransactionRollback ×100 | 37 ms |
| CheckpointLoad medium | load 363 ms |
| ActiveReplicas 100 | reconcile 1.88 s; converged |
| HistoricalSessionChurn 2000 | build 454 ms; checkpoint 254 KB |

Full **1M** fragmented history remains a stretch goal (visible-list shifts + apply snapshot cost); see [README](README.md#path-to-1m-fragmented-history).
