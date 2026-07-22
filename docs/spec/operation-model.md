# Operation Model Specification

**Normative for Phase 1 reference oracle and all later production kernels.**  
Optimized stores must produce identical visible state and operation traces for every valid input accepted by this model.

## 1. Identifiers

### SessionId

- 128-bit value, treated as an opaque big-endian byte sequence for ordering.
- Produced by CSPRNG when a document is opened for writing.
- Never restored as a writable identity from checkpoints.

### OpId

```
OpId := (SessionId, Clock)
Clock := UInt64 starting at 1 for each session
```

Comparison (total order):

1. Compare `Clock` ascending.
2. On equal clocks, compare `SessionId` as unsigned big-endian bytes ascending.

Identical `OpId` values must always carry identical payloads. A conflicting payload for an existing `OpId` is a **replica fork** and rejects the applying batch atomically.

### Contiguous streams

For each session, integrated clocks form `{1..N}` with no holes. An operation with clock `C` may integrate only after clocks `1..C-1` of the same session are integrated (or present in the same atomic batch in increasing order).

## 2. State vectors

A state vector maps `SessionId → UInt64` where the value is the highest contiguous integrated clock for that session.

- Operations above the frontier may be retained as **pending** under quotas.
- Encoding “update since vector V” emits every integrated operation not covered by V, including deletes and map/root ops.

## 3. Operation kinds

Every operation consumes exactly one clock (ranges expand to one identity per element for the oracle; production may compress consecutive inserts with identical parents into ranges that expand equivalently).

| Kind | Fields |
|---|---|
| `RootDeclare` | `Name`, `Kind` ∈ {Map, Array, Text} |
| `MapSet` | `MapId`, `Key`, `Value` |
| `SeqInsert` | `ContainerId`, `LeftOrigin`, `RightOrigin`, `Content` |
| `SeqDelete` | `TargetId` |

`ContainerId` / `MapId` are either a root name (resolved after root declaration) or a nested node `OpId` created by an attaching insert/set. Phase 1 reference model uses a single root text sequence and a single root map for simplicity; production expands nesting per the design doc without changing identity/order rules below.

### Values (reference scalars)

- `Null`
- `Bool`
- `Int64`
- `Float64` finite only; `-0.0` canonicalizes to `+0.0`
- `String` valid Unicode; equality is ordinal (code-unit) on the UTF-16 representation used by the host

## 4. Lamport sources

Each transaction `T` carries:

- `Lamport` (`UInt64`)
- `SourceOpId` (the maximum-Lamport operation observed when `T` began, or absent for the first op of an empty doc)

Validation on receive (checked arithmetic):

```
expected = max(previousIntegratedLamportForSession, source.Lamport) + 1
```

Reject the batch if `T.Lamport != expected` or on overflow. Map winner order is `(Lamport asc, OpId asc)` with the **maximum** tuple winning.

## 5. Root kind resolution

Roots are keyed by canonical `Name` (ordinal string compare).

- First integrated declaration creates the root.
- Later same-kind declarations are no-ops (coalesce).
- Concurrent different-kind declarations: the declaration with the **minimum OpId** wins; losers remain in history; observers surface a conflict warning; wrong-kind accessors fail.

## 6. Map winner rules

For each `(MapId, Key)`, retain all assignments as history. Visible value is the assignment with the greatest `(Lamport, OpId)`.

### Map removal (beta)

This beta does **not** define a map-key removal / `MapDelete` operation. Native v1 has no removal marker, and adding one would be a wire break. Hosts that need “clear” semantics should overwrite the key with a new `MapSet` (for example a `Null` scalar). Prior assignments remain in history and are never garbage-collected in v1. A dedicated remove op is deferred until a later wire revision.

## 7. YATA sequence integration

This section defines the Phase 1 reference integrator. It is intentionally simple and must stay easy to audit.

### Item

```
Item := {
  Id: OpId,
  LeftOrigin: OpId?,   // null = beginning origin
  RightOrigin: OpId?,  // null = ending origin
  Content: Scalar | NestedPlaceholder,
  Deleted: bool
}
```

Deleted items remain addressable origins. Missing origins block integration (pending).

### Integration order

When integrating insert `i` with origins `(left, right)` into the already-integrated sequence:

1. Ensure `left` and `right` (if non-null) are present (possibly deleted).
2. Collect the open interval of currently ordered items strictly between the resolved `left` and `right` positions (skipping nothing—deleted items stay in the structural order).
3. Among conflicting concurrent inserts that share the same origin interval, order by **OpId ascending** (see §1).
4. Place `i` at the unique position consistent with: after `left`, before `right`, and sorted among concurrent peers by OpId.

Equivalently for the oracle: maintain a linked structural list including tombstones; on insert, start after `left` (or head), walk until `right` (or tail), and insert before the first peer whose `OpId > i.Id` that also claims an origin incompatible with sitting after `i`, using the classic YATA “skip ancestors that are not i’s right origin” rule:

**Reference rule (YATA-style):**

Let `i.left` / `i.right` be origins. Scanning candidates `c` currently between those origins:

- Skip `c` if `c` is an ancestor of `i` in the “created after left toward right” sense used by YATA (oracle implements: skip `c` when `c.Id` is less than `i.Id` **and** `c` was inserted with a left origin that is still before `i`’s insertion point)—see executable model for the exact predicate.
- Otherwise, if `c.Id > i.Id`, insert `i` before `c`.
- If the scan reaches `i.right`, insert before that boundary.

The executable oracle is authoritative when prose and code disagree.

### Deletes

`SeqDelete(target)` marks `target` deleted. Idempotent. Visible enumeration skips deleted items.

### Range splitting

If production compresses consecutive inserts, applying a delete to a middle element must split the range into equivalent singleton identities. The oracle stores singletons only, so splitting is trivial and defines the expected post-split identity set.

## 8. Pending dependencies

An operation is pending when:

- Same-session previous clock is missing, or
- A referenced origin / Lamport source / container is missing.

Pending ops are retained under quotas and rechecked after each successful integration. Atomic batch apply either integrates the whole batch’s newly integrable subset consistently with store rules or rejects; production documents exact pending vs integrated return statuses.

## 9. Checkpoints

- `EncodeFullState` emits every integrated operation (or an equivalent encoding).
- `CreateFromCheckpoint` requires empty state, integrates the checkpoint, and starts a **new** writer session.
- `ApplyUpdate` always merges; it never replaces document state.

## 10. Idempotence and convergence

For any set of valid transactions from honest sessions:

- Delivering each update any positive number of times, in any order that eventually supplies dependencies, yields the same visible state and the same integrated OpId set.
- Duplicate identical operations are ignored.
- The deterministic simulator under `tests/Concordant.Model.Tests/Simulation` is the mechanical gate for this claim.

## 11. Out of scope for the Phase 1 oracle

- Native binary codec bytes
- Nested ownership graph beyond the simplified root text + root map surface
- Undo/redo stacks
- Quota enforcement details (production kernel)
- Persistence durability

These remain specified in the design doc and are added in later phases without changing §§1–10.
