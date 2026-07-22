# Changelog

All notable changes to Concordant are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html)
with a `0.x` / `-beta` prerelease line until 1.0.

## [0.1.0-beta.2] — 2026-07-21

### Added

- Canonical `ConcordantDocument.EncodeStateVector` / `TryDecodeStateVector` (24-byte little-endian entries; see [offline sync](docs/guides/offline-sync.md)).
- Atomic local `Transact`: staged overlay + bulk commit; failed callbacks roll back store, writer clock, frontier, pending, and undo notifications.
- Keyed pending/dependency indexes and a ready queue for deferred integration.
- Sequence ownership index and YATA structural order + visible UTF-16 rank helpers.
- Benchmark workloads: pending integration, sequential insert, random insert/delete, checkpoint load, transaction rollback; `--limit-smoke` for fast gates.
- Release hardening: package validation in CI, coverage baseline, `SECURITY.md`, Dependabot.

### Changed

- Medium remote-apply / checkpoint budgets re-baselined after kernel indexing (see [benchmarks](docs/benchmarks/README.md)); small batches still target p95 &lt; 16 ms.
- Fragmented-history release gate raised to **100k ops** (plan target 1M remains a stretch goal with a documented path).
- Map removal semantics documented for this beta (no new `MapDelete` wire op).
- Quickstart uses Core state-vector APIs instead of a private encoder.

### Fixed

- Deferred retention quotas (`MaxOperations` / `MaxHistoricalSessions`) no longer leave ops permanently pending after a failed integrate.
- Caller-serialization guard is atomic (`Interlocked`) so concurrent misuse fails predictably.
- SharedText insert/delete no longer rebuild the full visible string on every edit (rank-index bounds checks).

### Compatibility

- Native v1 wire bytes remain the interchange format; `net8.0` and `net10.0` stay byte-identical for the same op list.
- Additive public APIs only relative to `0.1.0-beta.1` (state-vector helpers). Transaction failure semantics are stricter (all-or-nothing) — hosts that relied on partial commit after a thrown callback must treat that as a bugfix.

## [0.1.0-beta.1] — 2026-07-19

### Added

- First public prerelease: shared text/map/array, nested values, native v1 codec, state vectors, selective undo, quotas, persistence abstractions.
- Dual-target packages for `net8.0` / `net10.0`.
