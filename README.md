# Recognition

A deterministic, governed, encrypted browser runtime. Local-first, offline-first, evidence-producing. Every operation emits a deterministic receipt; nothing important exists without integrity.

Canonical direction: see `docs/CANONICAL_HANDOFF_V1.md`.
Current audit and roadmap: see `docs/RECOGNITION_AUDIT_V1.md`.

## Layout

| Path | Purpose |
|---|---|
| `scripts/` | Production scripts (versioned, never mutated once sealed) |
| `scripts/_scratch/` | Historical runners/patches (kept for provenance) |
| `docs/` | Spec, audit, status documents |
| `proofs/` | Receipts, freezes, attestations, transcripts, seal index |
| `test_vectors/` | Golden and negative vectors |
| `profiles/` | Encrypted profile stores (ignored by git) |
| `runtime/` | Volatile runtime state (ignored by git) |

## Requirements

- Windows PowerShell 5.1 for the v1 sealed stack.
- **PowerShell 7.2+ (`pwsh`) for all v2 scripts** — the crypto core v2 uses .NET `AesGcm`/`HKDF`, which do not exist on .NET Framework.

## Conventions

- Scripts are versioned (`_v1`, `_v2`); a sealed version is never edited, it is superseded.
- Every meaningful operation appends a receipt to `proofs/receipts/*.ndjson` (append-only, UTF-8 no BOM, LF).
- Green paths print stable tokens (e.g. `SELFTEST_..._OK`); runners fail hard on missing tokens.
- Secrets are never passed on command lines in v2 — use the `RECOGNITION_PASSPHRASE` environment variable.

## Quick start (v2)

```powershell
# in pwsh 7+
$env:RECOGNITION_PASSPHRASE = "<passphrase>"
pwsh -NoProfile -File scripts/_selftest_recognition_crypto_v2.ps1 -RepoRoot .
pwsh -NoProfile -File scripts/_selftest_recognition_encrypted_profile_v2.ps1 -RepoRoot .
```
