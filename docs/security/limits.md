# Quotas and adversarial limits

Hosts must configure `ConcordantDocumentOptions` for untrusted updates. Caps cover:

| Option | Default | Purpose |
|---|---|---|
| `MaxUpdateBytes` | 64 MiB | Bound decode before parsing |
| `MaxOperations` | 10,000,000 | Integrated op retention |
| `MaxHistoricalSessions` | 100,000 | State-vector session cardinality |
| `MaxPendingOperations` | 100,000 | Dependency / sparse-clock backlog (count) |
| `MaxPendingBytes` | 64 MiB | Pending payload bytes (approx. string content) |
| `MaxClockGap` | 100,000 | Reject sparse clocks too far ahead of frontier |
| `MaxNestingDepth` | 64 | Attached shared-type depth |
| `MaxContentUtf16Length` | 10,000,000 | Single string / text chunk size (UTF-16 units) |

Rejected updates leave **zero** partial mutation. Prefer failing closed on untrusted peers; raise caps only when the host trusts the producer.

## Always-safe normalization (v1)

`Concordant.Internal.Normalization` performs **only** coalescing and deduplication:

- Identical `OpId` payloads in a batch coalesce.
- Conflicting `OpId` payloads reject as `ReplicaFork`.
- Pending entries already present in the integrated store are compacted via `ConcordantDocument.Normalize()`.
- **Tombstones and map assignment history are never garbage-collected** in v1.

## Adversarial coverage

Fuzz smoke (`dotnet run --project tests/Concordant.Fuzz.Tests -- --smoke`) exercises native bytes and custom-codec batches under time/allocation ceilings. Adversarial unit tests cover sparse clocks, dependency bombs, session churn, deep nesting, extreme lengths, Lamport chains, forked IDs, observer failures, and cap exhaustion.

See also [offline sync](../guides/offline-sync.md) and [native-v1](../format/native-v1.md).
