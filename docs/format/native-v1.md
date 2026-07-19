# Native binary update format (v1)

**Status:** Stable wire contract for `0.1` beta  
**Codec:** `Concordant.Sync.Native.NativeUpdateCodec`  
**Endianness:** Little-endian for all multi-byte integers except `SessionId` (big-endian 128-bit)

## Header (16 bytes)

| Offset | Size | Field |
|--------|------|--------|
| 0 | 4 | Magic `CNCR` (`43 4E 43 52`) — Concordant native format identifier |
| 4 | 2 | Version `u16 LE` (currently `1`) |
| 6 | 1 | Kind: `1` = update (delta), `2` = full-state checkpoint |
| 7 | 1 | Reserved (`0`) |
| 8 | 4 | Required features `u32 LE` |
| 12 | 4 | Optional features `u32 LE` |

Then:

| Field | Encoding |
|-------|----------|
| `opCount` | `u32 LE` |
| ops | `opCount` concatenated operation records |

## Feature negotiation

- If any **required** feature bit is unknown to the decoder, reject with `UnsupportedVersion`.
- Unknown **optional** bits are ignored.
- v1 emits required=`0`, optional=`0`.

## Operation record

Common prefix after op-kind byte:

1. `OpId` = `SessionId` (16 bytes BE) + `clock` (`u64 LE`, 1-based)
2. `lamport` (`u64 LE`)
3. `lamportSource` optional: `0` or `1` + `OpId`

| Kind byte | Payload |
|-----------|---------|
| `1` RootDeclare | UTF-8 name + `RootKind` (`1` Map, `2` Array, `3` Text) |
| `2` MapSet | `ContainerRef` + UTF-8 key + `Content` |
| `3` SeqInsert | `ContainerRef` + optional left/right origins + `Content` |
| `4` SeqDelete | target `OpId` |

### ContainerRef

- `0` + UTF-8 root name
- `1` + nested `OpId`

### Content

- `0` scalar: `ScalarKind` + payload
- `1` nested: `RootKind`

### Canonical scalars

| Kind | Payload |
|------|---------|
| `0` Null | — |
| `1` Bool | `0` / `1` |
| `2` Int64 | `i64 LE` |
| `3` Float64 | IEEE bits `u64 LE`; finite only; **canonical +0** (negative zero rejected) |
| `4` String | `u32 LE` UTF-8 byte length + UTF-8 bytes (must be valid Unicode; no lone surrogates) |

Strings are length-bounded by `MaxContentUtf16Length` (UTF-16 code units after decode).

## Semantics

- `EncodeUpdateSince` emits kind=`Update` with ops whose clocks exceed the remote state vector.
- `EncodeFullState` emits kind=`Checkpoint` with every integrated op (deterministic `OpId` order).
- `ApplyUpdate` always **merges**; it never replaces state. Checkpoint and update payloads both merge.
- `CreateFromCheckpoint` requires an empty document, requires kind=`Checkpoint`, then starts a **new** writer session.
- Empty `opCount=0` payloads are valid and apply as `Duplicate`.
- Decoders reject trailing bytes, truncated input, oversize payloads (`MaxUpdateBytes`), and unsupported versions without mutating the store.

## Host guidance

- Bound every decode with `MaxUpdateBytes` / `MaxOperations` / `MaxContentUtf16Length` from `ConcordantDocumentOptions`.
- Treat `Rejected` + `MalformedUpdate` / `UnsupportedVersion` / `ReplicaFork` as non-retryable on the same bytes.
- Prefer state-vector diffs for online peers; use checkpoints for catch-up and durable recovery snapshots.

## Compatibility

`net8.0` and `net10.0` must produce identical bytes for the same canonical operation list. Golden fixtures under `tests/Concordant.Core.Tests/Fixtures/Sync/` gate this. See [compatibility](../compatibility.md) and [support policy](../support-policy.md).
