# Support policy (.NET 8 / .NET 10)

## Supported targets

Public packages `Concordant.Core` and `Concordant.Persistence.Abstractions` multi-target:

| TFM | Support |
|---|---|
| `net8.0` | Supported (LTS-aligned consumer target) |
| `net10.0` | Supported (current SDK / runtime target) |

Behavior, public APIs, and the native v1 wire format are **equivalent** across both TFMs. Conditional code is allowed only for measured runtime optimizations that preserve oracle traces and wire bytes.

## SDK

Development and CI use the .NET 10 SDK pinned by `global.json`. Dual-target tests require both .NET 8 and .NET 10 runtime packs installed.

## Pre-1.0 stability

Until 1.0:

- SemVer applies with a `0.x` / `-beta` prerelease suffix.
- Breaking API or wire changes may land between minors; they will be called out in release notes and [compatibility](compatibility.md).
- Native format major version remains `1` for this beta line unless a new codec version is introduced with negotiation.

## What is not supported in v1

Networking, storage adapters, rich text, schemas, presence, encryption, ecosystem codecs (Yjs/Automerge), and destructive tombstone GC are out of scope. Persistence abstractions define contracts only; hosts supply durable implementations.
