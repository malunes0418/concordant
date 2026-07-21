<a id="readme-top"></a>

[![Contributors][contributors-shield]][contributors-url]
[![Forks][forks-shield]][forks-url]
[![Stargazers][stars-shield]][stars-url]
[![Issues][issues-shield]][issues-url]
[![MIT License][license-shield]][license-url]
[![.NET][dotnet-shield]][dotnet-url]



<div align="center">
  <h3 align="center">Concordant</h3>

  <p align="center">
    An embeddable, correctness-first CRDT framework for .NET 8 and .NET 10.
    <br />
    Shared text, maps, arrays, and nested containers; selective local undo; offline merge; and transport-agnostic sync over opaque update bytes—backed by a transactional YATA-style operation store.
    <br />
    <br />
    <a href="docs/design/concordant-framework.md"><strong>Explore the docs »</strong></a>
    <br />
    <br />
    <a href="samples/Concordant.Quickstart">View Quickstart</a>
    ·
    <a href="https://github.com/malunes0418/concordant/issues">Report Bug</a>
    ·
    <a href="https://github.com/malunes0418/concordant/issues">Request Feature</a>
  </p>
</div>



<details>
  <summary>Table of Contents</summary>
  <ol>
    <li>
      <a href="#about-the-project">About The Project</a>
      <ul>
        <li><a href="#built-with">Built With</a></li>
        <li><a href="#packages--status">Packages &amp; Status</a></li>
      </ul>
    </li>
    <li>
      <a href="#getting-started">Getting Started</a>
      <ul>
        <li><a href="#prerequisites">Prerequisites</a></li>
        <li><a href="#installation">Installation</a></li>
      </ul>
    </li>
    <li><a href="#usage">Usage</a></li>
    <li><a href="#roadmap">Roadmap</a></li>
    <li><a href="#documentation">Documentation</a></li>
    <li><a href="#contributing">Contributing</a></li>
    <li><a href="#license">License</a></li>
    <li><a href="#contact">Contact</a></li>
    <li><a href="#acknowledgments">Acknowledgments</a></li>
  </ol>
</details>



## About The Project

Concordant is a library you embed in your own .NET hosts. You own the wire and durability; Concordant owns the document model, merge semantics, and native update codec.

Features:

* **Shared types** — `SharedText`, `SharedMap`, `SharedArray`, with nested maps/arrays/text
* **Transactional document** — mutate inside `ConcordantDocument.Transact`, integrate with `Apply` / `ApplyUpdate`
* **Selective undo** — session-local `UndoManager` (remote updates are never stacked; undo is not checkpointed)
* **Offline merge** — encode deltas with `EncodeUpdateSince`, apply peer bytes with `ApplyUpdate`
* **Transport-agnostic sync** — you own the wire (HTTP, WebSockets, files, etc.); Concordant speaks update bytes
* **Persistence abstractions** — `IConcordantAppendLog` and `IConcordantCheckpointStore` for host-owned durability
* **Multi-target** — public packages target `net8.0` and `net10.0` with equivalent APIs and native v1 wire format

**Not in this prerelease:** networking stacks, production storage adapters, rich text, schemas, presence, encryption, ecosystem codecs, or destructive tombstone GC.

<p align="right">(<a href="#readme-top">back to top</a>)</p>



### Built With

* [![.NET][dotnet-shield]][dotnet-url]
* [.NET 8](https://dotnet.microsoft.com/download/dotnet/8.0) and [.NET 10](https://dotnet.microsoft.com/download/dotnet/10.0) (dual-target TFMs)
* xUnit (tests), BenchmarkDotNet (benchmarks)

<p align="right">(<a href="#readme-top">back to top</a>)</p>



### Packages & Status

**`0.1.0-beta.2`** — kernel stabilization prerelease (atomic transactions, scaling indexes, state-vector APIs, release gates). APIs may still change before 1.0.

| Package | Role |
|---|---|
| [`Concordant.Core`](src/Concordant.Core) | Document kernel, shared types, native codec, undo |
| [`Concordant.Persistence.Abstractions`](src/Concordant.Persistence.Abstractions) | Append-log / checkpoint host contracts |

<p align="right">(<a href="#readme-top">back to top</a>)</p>



## Getting Started

Clone the repo (or install the NuGet packages) and follow the steps below to build, test, and try the sample locally.

### Prerequisites

* [.NET 10 SDK](https://dotnet.microsoft.com/download) (pinned in `global.json`; roll-forward allowed)
* .NET 8 runtime / targeting pack for dual-target restore, build, and test

### Installation

1. Clone the repo
   ```sh
   git clone https://github.com/malunes0418/concordant.git
   cd concordant
   ```
2. Restore, build, and test
   ```sh
   dotnet restore Concordant.slnx
   dotnet build Concordant.slnx --configuration Release --no-restore
   dotnet test Concordant.slnx --framework net8.0 --configuration Release --no-build
   dotnet test Concordant.slnx --framework net10.0 --configuration Release --no-build
   ```
3. Optional smoke / sample
   ```sh
   dotnet run --project tests/Concordant.Fuzz.Tests --framework net8.0 --configuration Release -- --smoke
   dotnet run --project samples/Concordant.Quickstart --framework net8.0 --configuration Release
   ```

#### Install from packages

[`Concordant.Core`](https://www.nuget.org/packages/Concordant.Core) and [`Concordant.Persistence.Abstractions`](https://www.nuget.org/packages/Concordant.Persistence.Abstractions) are published on [nuget.org](https://www.nuget.org/) (current line: **`0.1.0-beta.2`** once this release is tagged; **`0.1.0-beta.1`** is already available).

```sh
dotnet add package Concordant.Core --version 0.1.0-beta.2
dotnet add package Concordant.Persistence.Abstractions --version 0.1.0-beta.2
```

To pack locally from source:

```sh
dotnet pack Concordant.slnx --configuration Release
```

<p align="right">(<a href="#readme-top">back to top</a>)</p>



## Usage

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
byte[] frontier = doc.EncodeStateVector(); // canonical 24-byte-entry layout

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

See the [offline sync guide](docs/guides/offline-sync.md) and the runnable sample [`samples/Concordant.Quickstart`](samples/Concordant.Quickstart).

### Project structure

```
src/Concordant.Core/                      Document kernel & shared types
src/Concordant.Persistence.Abstractions/  Append-log & checkpoint contracts
samples/Concordant.Quickstart/            Durability / recovery demo
tests/                                    Core, model, persistence, fuzz tests
docs/                                     Design, ADR, format, guides, policy
benchmarks/                               Performance harness docs & projects
.github/workflows/                        CI
```

<p align="right">(<a href="#readme-top">back to top</a>)</p>



## Roadmap

- [x] Document kernel with shared text/map/array and nested containers
- [x] Native format v1 codec and transport-agnostic update bytes
- [x] Session-local selective undo
- [x] Persistence abstractions (`IConcordantAppendLog`, `IConcordantCheckpointStore`)
- [x] Dual-target packages for `net8.0` / `net10.0`
- [x] Atomic local transactions + indexed pending/YATA paths (`0.1.0-beta.2`)
- [x] Canonical state-vector encode/decode APIs (`0.1.0-beta.2`)
- [ ] Production storage adapters (target: `beta.3`, prefer SQLite)
- [ ] Reference host recovery/sync sample
- [ ] Networking stacks
- [ ] Rich text, schemas, presence
- [ ] Encryption and ecosystem codecs
- [ ] Destructive tombstone GC

See the [open issues](https://github.com/malunes0418/concordant/issues) for a full list of proposed features (and known issues). Pre-1.0 stability notes live in the [support policy](docs/support-policy.md) and [compatibility](docs/compatibility.md) docs. Release history: [CHANGELOG.md](CHANGELOG.md).

<p align="right">(<a href="#readme-top">back to top</a>)</p>



## Documentation

- [Changelog](CHANGELOG.md)
- [Design](docs/design/concordant-framework.md)
- [ADR 0001: Transactional struct store](docs/adr/0001-transactional-struct-store.md)
- [Operation model](docs/spec/operation-model.md)
- [Native format v1](docs/format/native-v1.md)
- [Security policy](SECURITY.md)
- [Security limits](docs/security/limits.md)
- [Offline sync](docs/guides/offline-sync.md)
- [Support policy](docs/support-policy.md)
- [Compatibility](docs/compatibility.md)
- [Coverage baseline](docs/coverage/README.md)
- [Benchmarks](docs/benchmarks/README.md)

<p align="right">(<a href="#readme-top">back to top</a>)</p>



## Contributing

Contributions are what make the open source community such an amazing place to learn, inspire, and create. Any contributions you make are **greatly appreciated**.

Issues and pull requests are welcome while the project is in early beta. Prefer small, well-tested changes that preserve dual-target (`net8.0` / `net10.0`) behavior and the native v1 wire format.

1. Fork the Project
2. Create your Feature Branch (`git checkout -b feature/AmazingFeature`)
3. Restore, build, and test as in [Getting Started](#getting-started)
4. Commit your Changes (`git commit -m 'Add some AmazingFeature'`)
5. Push to the Branch (`git push origin feature/AmazingFeature`)
6. Open a Pull Request


### Publishing to NuGet.org

Releases are published from GitHub Actions via [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing) (no long-lived API keys in the repo).

1. On [nuget.org](https://www.nuget.org/) as user **bmalunes**, open **Trusted Publishing** and create a policy with:
   - **Owner:** you (`bmalunes`), or the org that owns the packages
   - **Repository Owner:** `malunes0418`
   - **Repository:** `concordant`
   - **Workflow File:** `publish.yml` (filename only; corresponds to `.github/workflows/publish.yml`)
   - **Environment:** `nuget` (must match the GitHub Environment name below)
2. Create a GitHub Environment named **nuget** under [Settings -> Environments](https://github.com/malunes0418/concordant/settings/environments) (protection rules can be empty). The `publish` job sets `environment: nuget` so the OIDC token matches the nuget.org policy.
3. Publish a [GitHub Release](https://github.com/malunes0418/concordant/releases), or run the **publish** workflow via **Actions > publish > Run workflow**.

Don't forget to give the project a star! Thanks again!

<p align="right">(<a href="#readme-top">back to top</a>)</p>



## License

Distributed under the MIT License. See [`LICENSE`](LICENSE) for more information.

Copyright (c) 2026 Concordant Contributors.

There is no separate third-party notices file in the repository at this time; NuGet packages declare `PackageLicenseExpression=MIT`.

<p align="right">(<a href="#readme-top">back to top</a>)</p>



## Contact

Concordant Contributors — [github.com/malunes0418](https://github.com/malunes0418)

Project Link: [https://github.com/malunes0418/concordant](https://github.com/malunes0418/concordant)

<p align="right">(<a href="#readme-top">back to top</a>)</p>



## Acknowledgments

* [Best-README-Template](https://github.com/othneildrew/Best-README-Template)
* [YATA](https://www.researchgate.net/publication/310212186_Near_Real-Time_Peer-to-Peer_Shared_Editing_on_Extensible_Data_Types) — operation ordering inspiration for the document store
* [Img Shields](https://shields.io)
* [.NET](https://dotnet.microsoft.com/)

<p align="right">(<a href="#readme-top">back to top</a>)</p>



<!-- MARKDOWN LINKS & IMAGES -->
[contributors-shield]: https://img.shields.io/github/contributors/malunes0418/concordant.svg?style=for-the-badge
[contributors-url]: https://github.com/malunes0418/concordant/graphs/contributors
[forks-shield]: https://img.shields.io/github/forks/malunes0418/concordant.svg?style=for-the-badge
[forks-url]: https://github.com/malunes0418/concordant/network/members
[stars-shield]: https://img.shields.io/github/stars/malunes0418/concordant.svg?style=for-the-badge
[stars-url]: https://github.com/malunes0418/concordant/stargazers
[issues-shield]: https://img.shields.io/github/issues/malunes0418/concordant.svg?style=for-the-badge
[issues-url]: https://github.com/malunes0418/concordant/issues
[license-shield]: https://img.shields.io/github/license/malunes0418/concordant.svg?style=for-the-badge
[license-url]: https://github.com/malunes0418/concordant/blob/main/LICENSE
[dotnet-shield]: https://img.shields.io/badge/.NET-8%20%7C%2010-512BD4?style=for-the-badge&logo=dotnet&logoColor=white
[dotnet-url]: https://dotnet.microsoft.com/
