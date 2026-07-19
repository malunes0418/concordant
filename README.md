# Concordant

**Concordant** is an embeddable, correctness-first CRDT framework for **.NET 8** and **.NET 10**.

It gives you shared text, maps, arrays, and nested containers; selective local undo; offline merge; and transport-agnostic sync over opaque update bytes—backed by a transactional YATA-style operation store.

## Features

- **Shared types** — `SharedText`, `SharedMap`, `SharedArray`, with nested maps/arrays/text
- **Transactional document** — mutate inside `ConcordantDocument.Transact`, integrate with `Apply` / `ApplyUpdate`
- **Selective undo** — session-local `UndoManager` (remote updates are never stacked; undo is not checkpointed)
- **Offline merge** — encode deltas with `EncodeUpdateSince`, apply peer bytes with `ApplyUpdate`
- **Transport-agnostic sync** — you own the wire (HTTP, WebSockets, files, etc.); Concordant speaks update bytes
- **Persistence abstractions** — `IConcordantAppendLog` and `IConcordantCheckpointStore` for host-owned durability
- **Multi-target** — public packages target `net8.0` and `net10.0` with equivalent APIs and native v1 wire format

**Not in this prerelease:** networking stacks, production storage adapters, rich text, schemas, presence, encryption, ecosystem codecs, or destructive tombstone GC.

## Status

**`0.1.0-beta.1`** — first public prerelease. APIs may change before 1.0.

| Package | Role |
|---|---|
| [`Concordant.Core`](src/Concordant.Core) | Document kernel, shared types, native codec, undo |
| [`Concordant.Persistence.Abstractions`](src/Concordant.Persistence.Abstractions) | Append-log / checkpoint host contracts |

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (pinned in `global.json`; roll-forward allowed)
- .NET 8 runtime / targeting pack for dual-target restore, build, and test

## Setup

```bash
git clone https://github.com/malunes0418/concordant.git
cd concordant

dotnet restore Concordant.slnx
dotnet build Concordant.slnx --configuration Release --no-restore
dotnet test Concordant.slnx --framework net8.0 --configuration Release --no-build
dotnet test Concordant.slnx --framework net10.0 --configuration Release --no-build
```

Optional smoke / sample:

```bash
dotnet run --project tests/Concordant.Fuzz.Tests --framework net8.0 --configuration Release -- --smoke
dotnet run --project samples/Concordant.Quickstart --framework net8.0 --configuration Release
```

## Install (packages)

Packages are intended for NuGet as **`0.1.0-beta.1`**. If they are not yet on [nuget.org](https://www.nuget.org/), pack locally:

```bash
dotnet pack Concordant.slnx --configuration Release
# then add a local source, or reference project/output nupkgs
```

Once published:

```bash
dotnet add package Concordant.Core --version 0.1.0-beta.1
dotnet add package Concordant.Persistence.Abstractions --version 0.1.0-beta.1
```

## Quickstart

### Shared text, map, and sync

```csharp
using Concordant;
using Concordant.Shared;
using Concordant.Values;

using var doc = new ConcordantDocument();

_ = doc.Transact(tx =>
{
    SharedText notes = tx.GetOrCreateText("notes");
    notes.Insert(0, "hello");

    SharedMap meta = tx.GetOrCreateMap("meta");
    meta.Set("rev", ConcordantScalar.Int64(1));

    SharedArray tags = tx.GetOrCreateArray("tags");
    tags.Add(ConcordantScalar.String("draft"));
});

// Empty remote state vector => full missing update for peers that know nothing.
byte[] update = doc.EncodeUpdateSince(new Dictionary<SessionId, ulong>());

using var peer = new ConcordantDocument();
ApplyResult result = peer.ApplyUpdate(update);
// result.Status is Integrated / Duplicate / Pending / Rejected, etc.

Console.WriteLine(peer.GetText("notes")); // "hello"
```

### Nested types

```csharp
_ = doc.Transact(tx =>
{
    SharedMap root = tx.GetOrCreateMap("doc");
    SharedMap chapter = root.CreateMap("ch1");
    SharedText body = chapter.CreateText("body");
    body.Insert(0, "Nested text");
});
```

### Selective undo

```csharp
using Concordant.History;

using var doc = new ConcordantDocument();
using var undo = new UndoManager(doc);

_ = doc.Transact(tx =>
{
    SharedText t = tx.GetOrCreateText("notes");
    t.Insert(0, "hello");
});

if (undo.CanUndo)
{
    UndoResult ur = undo.Undo();
}
```

### Offline merge + persistence boundary

Hosts append **after** a successful in-memory commit. Memory is not rolled back on a failed append—retry the same update bytes. Recovery loads a checkpoint with `ConcordantDocument.CreateFromCheckpoint`, then replays the append-log tail via `ApplyUpdate`.

```csharp
using Concordant.Persistence;

// After Transact(...):
byte[] delta = doc.EncodeUpdateSince(remoteFrontier);
await appendLog.AppendAsync(delta); // host-owned IConcordantAppendLog

// Compact:
byte[] full = doc.EncodeFullState();
await checkpoints.SaveAsync(new ConcordantCheckpoint(full, stateVectorBytes, coveredLogSequence: tip));

// Recover:
ConcordantCheckpoint? cp = await checkpoints.TryLoadAsync();
using ConcordantDocument recovered = ConcordantDocument.CreateFromCheckpoint(cp!.FullState.Span);
await foreach (ConcordantLogEntry entry in appendLog.ReadFromAsync(cp.CoveredLogSequence))
{
    _ = recovered.ApplyUpdate(entry.Payload.Span);
}
```

See the [offline sync guide](docs/guides/offline-sync.md) and the runnable sample `samples/Concordant.Quickstart`.

## Project structure

```
src/Concordant.Core/                      Document kernel & shared types
src/Concordant.Persistence.Abstractions/  Append-log & checkpoint contracts
samples/Concordant.Quickstart/            Durability / recovery demo
tests/                                    Core, model, persistence, fuzz tests
docs/                                     Design, ADR, format, guides, policy
benchmarks/                               Performance harness docs & projects
.github/workflows/                        CI
```

## Documentation

- [Design](docs/design/concordant-framework.md)
- [ADR 0001: Transactional struct store](docs/adr/0001-transactional-struct-store.md)
- [Operation model](docs/spec/operation-model.md)
- [Native format v1](docs/format/native-v1.md)
- [Security limits](docs/security/limits.md)
- [Offline sync](docs/guides/offline-sync.md)
- [Support policy](docs/support-policy.md)
- [Compatibility](docs/compatibility.md)
- [Benchmarks](docs/benchmarks/README.md)

## Contributing

Issues and pull requests are welcome while the project is in early beta. Prefer small, well-tested changes that preserve dual-target (`net8.0` / `net10.0`) behavior and the native v1 wire format.

1. Fork and clone the repository
2. Restore, build, and test as in [Setup](#setup)
3. Open a PR with a clear description of the change and how it was verified

## License

This project is licensed under the **MIT License** ([SPDX: `MIT`](https://spdx.org/licenses/MIT.html)).

Copyright (c) 2026 Concordant Contributors. See [LICENSE](LICENSE) for the full text.

There is no separate third-party notices file in the repository at this time; NuGet packages declare `PackageLicenseExpression=MIT`.

