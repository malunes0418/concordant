# Offline sync guide

Transport-agnostic merge for hosts that exchange opaque update bytes between replicas.

## Model

- Each `ConcordantDocument` is **caller-serialized** and **memory-only**.
- Writers use a CSPRNG `SessionId` (or a test-only `WriterSession`).
- Peers exchange either:
  - **Delta updates** from `EncodeUpdateSince(remoteStateVector)`, or
  - **Full-state checkpoints** from `EncodeFullState()` / `CreateFromCheckpoint`.
- `ApplyUpdate` always **merges**. It never replaces local state. Checkpoints and deltas use the same merge path.

## Typical peer loop

1. Persist or remember the peer's last known state vector.
2. Call `EncodeUpdateSince(peerStateVector)` and send the bytes.
3. On receive, call `ApplyUpdate(bytes)` and inspect `ApplyResult`:
   - `Integrated` — new ops merged; advance the peer frontier from `StateVector`.
   - `Duplicate` — already known; safe.
   - `PendingDependencies` — hold/retry after delivering `MissingRanges`.
   - `Rejected` — do not retry identical bytes unless `Retryable` (e.g. quota after raising limits).
4. Optionally send your state vector so the peer can compute the next diff.

## State vectors

`ConcordantDocument.StateVector` maps each historical session to its contiguous integrated clock frontier. Diffs are ops with clocks strictly greater than the remote entry for that session (or all ops for unknown sessions).

For durable checkpoint metadata and peer handshakes, use the canonical helpers:

- `EncodeStateVector()` / `EncodeStateVector(IReadOnlyDictionary<SessionId, ulong>)`
- `TryDecodeStateVector(ReadOnlySpan<byte>, out …)`

Layout (v1): `count:u32 LE`, then `count` entries sorted by `SessionId` ascending, each `(session:16 bytes big-endian, clock:u64 LE)`. Persistence abstractions still treat these bytes as opaque.

## Checkpoints and recovery

- `EncodeFullState()` emits every integrated op (deterministic `OpId` order).
- `CreateFromCheckpoint` requires an empty document, validates checkpoint kind, merges, then keeps a **fresh writer session** (never restores writable identity).
- Hosts that want durability should append update bytes via `Concordant.Persistence.Abstractions` and recover with checkpoint + log tail. See the quickstart sample and `DurabilityContract`.

## Selective local undo

`UndoManager` is **session-local** and **not** part of checkpoints or wire updates. Remote winners can produce `RemoteWinner` / `NoVisibleChange` outcomes. Do not expect undo stacks to survive process restart unless the host persists them separately (out of scope for v1).

## Limits for untrusted peers

Configure `ConcordantDocumentOptions` (`MaxUpdateBytes`, `MaxOperations`, `MaxHistoricalSessions`, pending/clock/nesting/content caps) before applying untrusted updates. See [security limits](../security/limits.md).

## Compatibility

`net8.0` and `net10.0` produce identical native wire bytes for the same canonical operations. Golden fixtures under `tests/Concordant.Core.Tests/Fixtures/Sync/` gate this. Format details: [native-v1](../format/native-v1.md).
