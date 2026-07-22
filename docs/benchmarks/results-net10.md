# Benchmark results — .NET 10

## Hardware / runtime

| Field | Value |
|---|---|
| CPU | 13th Gen Intel Core i5-13420H @ 2.10 GHz (8P+4E / 12 logical) |
| RAM | ~48 GB |
| OS | Windows 11 (10.0.26200) |
| SDK | .NET SDK 10.0.203 |
| Runtime | .NET 10.0.7, X64 RyuJIT AVX2 |
| Date | 2026-07-21 (`0.1.0-beta.2`) |

## Commands run

```bash
dotnet run --project benchmarks/Concordant.Benchmarks --framework net10.0 --configuration Release -- --filter "*" --exporters json --artifacts artifacts/bench-net10
dotnet run --project benchmarks/Concordant.Benchmarks --framework net10.0 --configuration Release -- --limit-smoke
dotnet run --project benchmarks/Concordant.Benchmarks --framework net10.0 --configuration Release -- --limit
```

BDN used the in-process `FastInProcessConfig` (InvocationCount=1). Raw JSON under `artifacts/bench-net10/results/`.

## BenchmarkDotNet (p50 / p95)

| Benchmark | p50 | p95 | Allocated / op |
|---|---:|---:|---:|
| LocalEdit_Small | 0.175 ms | 0.192 ms | 94 KB |
| LocalEdit_Medium | 0.725 ms | 0.806 ms | 1.4 MB |
| ApplyUpdate_Small | 1.39 ms | 1.48 ms | 243 KB |
| ApplyUpdate_Medium (~2k ops) | **65 ms** | **92 ms** | 4.1 MB |
| CheckpointLoad_Medium | **123 ms** | **125 ms** | 5.0 MB |
| EncodeFullState_Medium | 1.77 ms | 2.01 ms | 966 KB |
| EncodeUpdateSince_EmptyRemote_Medium | 3.26 ms | 3.45 ms | 1.0 MB |
| StateVectorSessions_Churn500 | 1.1 µs | 1.6 µs | 288 B |
| SequentialInsert_4k | 372 ms | 376 ms | 33 MB |
| RandomInsertDelete_2k | 389 ms | 416 ms | 666 MB |
| PendingIntegration_ApplyGapThenPrefix | 0.316 ms | 0.328 ms | 125 KB |
| TransactionRollback_Small | 0.375 ms | 0.386 ms | 237 KB |

### vs targets

- Local edit p95 &lt; 1 ms ✅
- Small remote batch p95 &lt; 16 ms ✅
- Medium remote batch: aspirational 16 ms **not met**; **beta.2 re-baseline** is p95 &lt; **100 ms** on this TFM (measured 92 ms; beta.1 was ~4.6 s)

## Limit workloads (one-shot)

| Workload | Result |
|---|---|
| FragmentedHistory **10k** (`--limit-smoke`) | build 1.17 s; checkpoint 947 KB; encode 13 ms; alloc ~50 MB |
| FragmentedHistory **100k** (beta.2 gate) | build **364 s**; checkpoint 9.5 MB; encode 103 ms; alloc **~1.65 GB** |
| SequentialInsert 4k | build 462 ms |
| RandomInsertDelete 2k | build 390 ms |
| PendingIntegration | gap=`PendingDependencies` → prefix=`Integrated`; pending_after=0 |
| TransactionRollback ×100 | 55 ms |
| CheckpointLoad medium | load 199 ms |
| ActiveReplicas 100 | reconcile 1.62 s; converged |
| HistoricalSessionChurn 2000 | build 472 ms; checkpoint 254 KB |

**Budgets (beta.2, reference laptop):** FragmentedHistory 100k — time &lt; 10 min, alloc &lt; 3 GB. Full **1M** remains stretch; path in [README](README.md#path-to-1m-fragmented-history).
