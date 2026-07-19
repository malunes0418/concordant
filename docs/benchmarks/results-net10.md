# Benchmark results — .NET 10

## Hardware / runtime

| Field | Value |
|---|---|
| CPU | 13th Gen Intel Core i5-13420H @ 2.10 GHz (8P+4E / 12 logical) |
| RAM | ~48 GB |
| OS | Windows 11 (10.0.26200) |
| SDK | .NET SDK 10.0.203 |
| Runtime | .NET 10.0.7, X64 RyuJIT AVX2 |
| Date | 2026-07-19 |

## Commands run

```bash
dotnet run --project benchmarks/Concordant.Benchmarks --framework net10.0 --configuration Release -- --filter "*" --exporters json --artifacts artifacts/bench-net10
dotnet run --project benchmarks/Concordant.Benchmarks --framework net10.0 --configuration Release -- --limit
```

BDN used the in-process `FastInProcessConfig` (InvocationCount=1). Raw JSON under `artifacts/bench-net10/results/`.

## BenchmarkDotNet (p50 / p95)

| Benchmark | p50 | p95 | Allocated / op |
|---|---:|---:|---:|
| LocalEdit_Small | 0.123 ms | 0.148 ms | 35 KB |
| LocalEdit_Medium | 0.618 ms | 0.708 ms | 414 KB |
| ApplyUpdate_Small | 1.04 ms | 1.09 ms | 131 KB |
| ApplyUpdate_Medium (~2k ops) | **4.57 s** | **4.59 s** | 2.1 MB |
| CheckpointLoad_Medium | **4.54 s** | **4.62 s** | 3.0 MB |
| EncodeFullState_Medium | 1.31 ms | 1.55 ms | 966 KB |
| EncodeUpdateSince_EmptyRemote_Medium | 0.808 ms | 1.12 ms | 1.0 MB |
| StateVectorSessions_Churn500 | 1.8 µs | 2.2 µs | 288 B |

Same pattern as .NET 8: local/small apply meet aspirational latency; medium apply/checkpoint do not.

## Limit workloads (one-shot)

| Workload | Result |
|---|---|
| FragmentedHistory **10k ops** (plan target 1M; proxy) | build 362 s; checkpoint 947 KB; encode 23 ms; ~1.6 GB alloc during build |
| ActiveReplicas 100 | reconcile 491 ms; converged |
| HistoricalSessionChurn 2000 | build 90 ms; checkpoint 254 KB; 2000 sessions |

Full 1M fragmented history was **not** completed in the release-gate window.
