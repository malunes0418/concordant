# Benchmark results — .NET 8

## Hardware / runtime

| Field | Value |
|---|---|
| CPU | 13th Gen Intel Core i5-13420H @ 2.10 GHz (8P+4E / 12 logical) |
| RAM | ~48 GB |
| OS | Windows 11 (10.0.26200) |
| SDK | .NET SDK 10.0.203 |
| Runtime | .NET 8.0.22 (8.0.2225.52707), X64 RyuJIT AVX2 |
| Date | 2026-07-19 |

## Commands run

```bash
dotnet run --project benchmarks/Concordant.Benchmarks --framework net8.0 --configuration Release -- --filter "*" --exporters json --artifacts artifacts/bench-net8
dotnet run --project benchmarks/Concordant.Benchmarks --framework net8.0 --configuration Release -- --limit
```

BDN used the in-process `FastInProcessConfig` (InvocationCount=1). Raw JSON under `artifacts/bench-net8/results/`.

## BenchmarkDotNet (p50 / p95)

| Benchmark | p50 | p95 | Allocated / op |
|---|---:|---:|---:|
| LocalEdit_Small | 0.112 ms | 0.126 ms | 33 KB |
| LocalEdit_Medium | 0.472 ms | 0.657 ms | 413 KB |
| ApplyUpdate_Small | 1.37 ms | 1.45 ms | 132 KB |
| ApplyUpdate_Medium (~2k ops) | **4.37 s** | **4.43 s** | 2.1 MB |
| CheckpointLoad_Medium | **4.43 s** | **4.52 s** | 3.0 MB |
| EncodeFullState_Medium | 2.10 ms | 2.24 ms | 964 KB |
| EncodeUpdateSince_EmptyRemote_Medium | 0.726 ms | 0.965 ms | 1.0 MB |
| StateVectorSessions_Churn500 | 1.6 µs | 1.9 µs | 0 B |

Plan aspirational targets: local edit p95 &lt; 1 ms ✅ (small/medium); normal remote batch p95 &lt; 16 ms ✅ for **small** only — **medium apply/checkpoint miss by orders of magnitude**.

## Limit workloads (one-shot)

| Workload | Result |
|---|---|
| FragmentedHistory **10k ops** (plan target 1M; proxy) | build 403 s; checkpoint 947 KB; encode 28 ms; ~1.6 GB alloc during build |
| ActiveReplicas 100 | reconcile 820 ms; converged |
| HistoricalSessionChurn 2000 | build 131 ms; checkpoint 254 KB; 2000 sessions |

Full 1M fragmented history was **not** completed in the release-gate window (sequence integration cost dominates).
