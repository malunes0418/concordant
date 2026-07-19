# ADR 0001: Transactional struct / operation store

- **Status:** Accepted
- **Date:** 2026-07-19
- **Deciders:** Product owner + peer-review arbiter (APPROVED)

## Context

The library must provide ordered shared text and arrays, maps, nested nodes, compact state-vector sync, selective local undo, and interactive latency budgets (~10 MB visible state, ~100 active replicas, ~1M historical operations; local edit &lt;1 ms p95 and normal remote batches &lt;16 ms p95 as benchmark budgets). Three core designs were considered:

1. **Transactional struct / operation store** (YATA-style): stable `(SessionId, Clock)` identities, contiguous writer streams, range-friendly encoding, deterministic integration.
2. **Immutable operation DAG**: excellent auditability; weaker interactive density and sync compactness for the stated targets.
3. **Pure delta-state CRDTs**: strong for registers/counters; awkward for ordered text/arrays and selective undo without rebuilding an operation layer.

## Decision

Adopt a **transactional YATA-style operation store** as the document kernel:

- Every insert, delete, map assignment, and root declaration is a clocked operation in a contiguous session stream.
- State vectors are contiguous integrated frontiers; missing causal predecessors wait as bounded pending data.
- Sequences use explicit left/right origins with deterministic tie-breaks; deleted origins remain addressable.
- Maps use Lamport-then-OpId LWW with validated Lamport sources.
- An intentionally simple **executable reference model** is the correctness oracle; optimized indexed/range-compressed storage may replace it only after identical traces.

## Consequences

### Positive

- One identity/integration engine shared by text, arrays, maps, and roots.
- State-vector diffs naturally include deletions.
- Selective undo can target exact operation IDs and reinsert at tombstoned anchors.
- Compact encoding of consecutive local inserts as ranges is straightforward.

### Negative / accepted costs

- Integration correctness is algorithm-sensitive; requires a written spec, reference oracle, and simulator before optimization.
- Tombstones and historical sessions grow until host-level epoch migration; destructive GC is out of v1 because membership/stability proofs are out of scope.
- Fresh writer sessions on each open prevent clock rollback reuse but increase historical-session churn, which must be quota-bounded and documented.

### Alternatives rejected

| Alternative | Why rejected |
|---|---|
| Immutable op DAG as primary store | Metadata growth and materialization cost vs interactive budgets |
| Delta-state-only | Insufficient for ordered sequences + selective undo without reintroducing ops |
| Persisted writable clocks restored from checkpoints | Risk of ID reuse after rollback / restore |

## Related

- [`../design/concordant-framework.md`](../design/concordant-framework.md)
- [`../spec/operation-model.md`](../spec/operation-model.md)
