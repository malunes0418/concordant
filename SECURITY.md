# Security Policy

## Supported Versions

| Version | Supported |
| --- | --- |
| `0.1.0-beta.x` | Yes (best-effort while pre-1.0) |
| older prereleases | No |

Until 1.0, Concordant is a beta library. Security fixes are prioritized for the current `0.1.0-beta.*` line on the default branch. See [docs/support-policy.md](docs/support-policy.md) for TFM and stability expectations.

## Reporting a Vulnerability

Please **do not** open a public GitHub issue for security vulnerabilities.

Prefer [GitHub private vulnerability reporting](https://github.com/malunes0418/concordant/security/advisories/new) for this repository. Include:

- A description of the issue and impact
- Steps to reproduce or a proof-of-concept if available
- Affected package versions / commit SHA if known

You should receive an acknowledgment within a few business days. After triage we will coordinate a fix and disclosure timing.

## Host responsibilities

Concordant is an embeddable CRDT kernel. Hosts that accept untrusted update bytes must configure quotas and treat rejected updates as hostile input. See [docs/security/limits.md](docs/security/limits.md).
