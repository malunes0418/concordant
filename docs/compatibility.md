# Compatibility and migration

## Wire format

- Native codec magic `CNCR`, version `1`.
- Unknown **required** feature bits → reject `UnsupportedVersion`.
- Unknown **optional** feature bits → ignored.
- `ApplyUpdate` merges both update and checkpoint payloads; it does not wipe local state.

See [native-v1](format/native-v1.md).

## Package versioning (0.1 beta)

| Change type | Expectation in 0.x |
|---|---|
| Bug fix / hardening | Patch or prerelease bump; wire-compatible |
| Additive public API | Allowed; prefer non-breaking |
| Breaking public API | Allowed before 1.0; documented in release notes |
| Native version bump | New version + negotiation; old bytes remain reject-or-decode per rules |

## Cross-TFM guarantee

For the same canonical operation list, `net8.0` and `net10.0` must emit **byte-for-byte identical** native updates/checkpoints. Divergence is a release blocker.

## Session / undo migration

- Writer `SessionId` is never restored from checkpoints.
- Undo history is session-local and absent from checkpoints; hosts must not assume undo survives recovery.
- Opening recovery always uses a fresh writer session (`DurabilityContract.RecoveryUsesFreshWriterSession`).

## Custom codecs

`IUpdateCodec` is marked experimental (`CNCR001`). Core always revalidates decoded batches. Custom codecs must not access store internals and must not be relied on for security boundaries—quotas still apply after decode.

## Upgrading from earlier 0.1 builds

This repository's first public prerelease is `0.1.0-beta.1`. There is no prior stable migration path. Future 0.1 builds that change wire or API will list explicit steps here.
