# Recognition — Audit v1 (against Canonical Handoff v1)

Date: 2026-07-10
Scope: full repo at C:\dev\recognition
Verdict: strong evidence/receipt scaffolding is real and proven; the crypto layer, evidence chain, and plaintext-hygiene guarantees do NOT yet meet the canonical spec. No actual browser engine exists yet — the runtime is a state/event ledger, which is fine for this phase, but several "proven" claims rest on synthetic hardcoded events.

## 1. What actually exists (verified by reading source + artifacts)

| Area | Status | Notes |
|---|---|---|
| Packet Constitution (build/verify/negative vectors) | GREEN | Deterministic packets, golden vectors, tamper vectors, freeze bundle with sha256sums |
| Receipt libraries (canonical JSON, NDJSON append) | GREEN | Hand-rolled canonical JSON with sorted keys |
| Runtime state (session/tabs/events) | GREEN | JSON state files + events.ndjson |
| Runtime bridge + replay + negative vectors | GREEN | Token-gated child process execution |
| Timeline materialize + verify | PARTIAL | Verifies seq continuity + UTC monotonicity only — no hashes |
| Attestation (ssh-keygen -Y, allowed_signers) | PARTIAL | Real signatures, but trust anchor ships inside the bundle it attests |
| Workbench (HTML snapshot + validator + freeze) | GREEN | Local only |
| Encrypted profile | PARTIAL | Works, but crypto deviates from spec (see F2) |
| Locked startup | PARTIAL | Depends on a script in scripts\_scratch\ |
| Clean browser session | PARTIAL | Events are 3 hardcoded literals; wipe is ordinary delete |

## 2. Findings (ordered by severity)

### F1 — CRITICAL: Plaintext persists after shutdown (violates spec §7, §19, §30)
- `runtime\` contains plaintext session state, tab state, events with URLs, and materialized timeline — at rest, right now.
- `runtime\encrypted_profile_selftest_value.txt` is a **decrypted vault value written to disk** by the profile `get` action and never cleaned up. The `get` action's only output mode is "write plaintext to a file."
- Receipts under `proofs\receipts\` embed browsing URLs in plaintext and persist forever.
- Spec law: "Nothing plaintext after shutdown." Currently almost everything interesting is plaintext after shutdown.

### F2 — CRITICAL: Cryptography does not match spec §8
- Spec: AES-256-GCM. Implemented: AES-256-CBC + HMAC-SHA256 (encrypt-then-MAC — sound, but not spec) via the deprecated `AesManaged` class.
- KDF is PBKDF2-**SHA1** (`Rfc2898DeriveBytes` default), self-labeled `PBKDF2-SHA1-200000`.
- MAC check compares base64 strings — not constant-time.
- No key hierarchy: spec requires per-profile master key → derived enc/HMAC keys. Implementation derives keys directly from the passphrase on every operation and re-encrypts the entire vault on every `put`. No per-object keys, no key rotation path, no encrypted manifests/indexes.

### F3 — CRITICAL: Secrets on process command lines
- `-Passphrase` is passed as a plain command-line argument through every layer (locked startup → profile script → clean session), visible in process listings and potentially in logs. The clean-session summary is also passed via `-Value` on the command line.
- Should move to stdin or environment with immediate erasure, plus DPAPI at rest on Windows.

### F4 — HIGH: No hash chain in events or timeline (violates spec §12)
- Spec: every event carries sequence, timestamp, hash, identity, previous hash. Implemented events have seq/ts/type/tab_id only. Timeline verification proves ordering, not integrity — any event's content can be silently edited without breaking verification, as long as seq/ts are preserved.
- Timeline receipt is written with `WriteAllText` (overwrite), not append — the one receipt stream that is not append-only.

### F5 — HIGH: Attestation trust anchor is self-contained
- `recognition_verify_runtime_bridge_attestation_v1.ps1` verifies the signature against the `allowed_signers` file **inside the same attestation directory**. An attacker who can modify the bundle can re-sign with their own key and replace allowed_signers; verification still passes. The trust root must be pinned outside the bundle (e.g., `proofs\trust\allowed_signers`, which the export already references but the verifier does not use).

### F6 — MEDIUM: "Clean browser session" is synthetic
- The session's three events are hardcoded string literals with fixed timestamps (2026-06-07). It proves the pipeline plumbing, not a browser session. "Plaintext wipe" is `Remove-Item -Recurse` — no secure deletion, and no wipe of the plaintext that was passed through command lines and child stdout logs (`proofs\runs\...` captures child stdout, which includes receipts echoing key names).

### F7 — MEDIUM: Production depends on scratch
- `recognition_locked_startup_v1.ps1` hard-codes `scripts\_scratch\RUN_RECOGNITION_RUNTIME_BRIDGE_SEAL_VERIFY_V1.ps1`. Scratch is also where the freeze runners, negative vector runners, and multitab capture live. These are production verifiers living in a folder named "scratch."

### F8 — MEDIUM: No version control, no committed spec
- No `.git`. For a project whose identity is "everything verifiable, everything reconstructable," the source itself has no history, no integrity, no recovery. The canonical handoff document is not in the repo. No README. `.bak_*` files scattered through `scripts\` and `proofs\receipts\`. Freeze bundles duplicate whole script trees inside the repo.

### F9 — Expected gaps (spec §32 — not defects, just not built)
Vault (canonical layout §7), encrypted filesystem, cookies, history, downloads, bookmarks, passwords, extension governance, identity vault, network policy, certificate manager, recovery, sync, packaging/installer/updater/release/license, backup, TRIAD/NeverLost integration. Layer 0 (Identity) has no standalone implementation — identity exists only as strings inside receipts.

## 3. Remediation roadmap (proposed order)

**Phase 0 — Repo integrity (half a day)**
git init + .gitignore (runtime/, tmp/, profiles/, proofs/runs/), commit canonical spec as docs/CANONICAL_HANDOFF_V1.md, README, promote needed _scratch scripts to scripts\, quarantine .bak files, fix F7.

**Phase 1 — Crypto core v2 (fixes F1, F2, F3)**
One shared `_lib_recognition_crypto_v1.ps1`:
- AES-256-GCM (requires PowerShell 7 / .NET `AesGcm`; PS 5.1 has no GCM — decide: require pwsh7, or keep EtM-CBC with PBKDF2-SHA256 + constant-time compare as documented fallback).
- Random 256-bit per-profile master key, wrapped by passphrase-derived KEK (PBKDF2-SHA256 or Argon2id); HKDF-derived per-domain subkeys; per-object nonces.
- Secrets via stdin/env only; `get` returns to stdout or encrypted output, never bare plaintext files; wipe selftest artifacts.
- Encrypt runtime state and timeline outputs at rest; scrub URLs from persistent receipts (hash them instead).

**Phase 2 — Evidence chain v2 (fixes F4)**
Event schema v2: `event_hash` (canonical JSON) + `prev_hash` + identity block. Chain verifier proving nothing modified/missing/reordered/forged (spec §15). Make every receipt stream append-only.

**Phase 3 — Trust root (fixes F5)**
Pinned `proofs\trust\allowed_signers` used by all verifiers; attestation bundles carry signatures only.

**Phase 4 — Recognition Vault (spec §7)**
Encrypted object store with canonical layout, encrypted manifest + index, receipts per object. Migrate the profile store onto it.

**Phase 5 — Engines on the vault**
History (append-only), cookies, downloads, bookmarks, passwords — each: encrypted objects + receipts + verification + recovery.

**Phase 6 — Real runtime bridge**
Replace synthetic events with an actual browser event source (CDP bridge to an existing browser or WebView2 shell) feeding the same event schema. Then recovery engine, sync, packaging.

## 4. Remediation status (2026-07-10)

Phase 0 and Phase 1 built:

- `docs/CANONICAL_HANDOFF_V1.md` — spec committed to repo.
- `README.md`, `.gitignore` — repo hygiene; volatile/plaintext dirs ignored.
- `scripts/recognition_seal_verify_v1.ps1` — seal verify promoted out of `_scratch` (F7).
- `scripts/_lib_recognition_crypto_v2.ps1` — AES-256-GCM, PBKDF2-SHA256 (600k) KEK, random per-profile master key wrapped under KEK, HKDF-SHA256 domain subkeys, per-object nonces, key zeroization (F2). Requires pwsh 7.2+.
- `scripts/recognition_encrypted_profile_v2.ps1` — per-item encryption, HMAC-indexed names (encrypted index), passphrase via `RECOGNITION_PASSPHRASE` env only, `get` prints to stdout and never writes plaintext files, receipts free of plaintext names/values (F1, F3).
- `scripts/recognition_locked_startup_v2.ps1` — env passphrase, v2 profile verify, promoted seal verify, GET-value lines scrubbed from run logs (F3, F7).
- `scripts/_selftest_recognition_crypto_v2.ps1` — 20+ checks incl. negative vectors: ct/tag/AAD/key tamper, wrong passphrase, keystore tamper, rekey.
- `scripts/_selftest_recognition_encrypted_profile_v2.ps1` — roundtrip, plaintext-leak scans of store and receipts, tamper/wrong-pass negatives, rekey.
- `scripts/RUN_PHASE1_GREEN_V2.ps1` — parse-gates all v2 scripts, runs both selftests. Token: `RECOGNITION_PHASE1_GREEN_V2_OK`.
- `scripts/RUN_PHASE0_GIT_INIT.ps1` — one-time git bootstrap (git cannot be initialized through the Cowork mount; run on Windows).
- Deleted `runtime/encrypted_profile_selftest_value.txt` (leaked decrypted vault value, F1).

Still open: F4 (event/timeline hash chain), F5 (pinned trust root), F6 (synthetic clean session), remaining plaintext-at-rest in `runtime/` and URL plaintext in v1 receipts (full fix lands with the Vault), and all Phase 4–6 systems.
