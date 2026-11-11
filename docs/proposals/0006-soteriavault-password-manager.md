# 0006 — SoteriaVault as Recognition's password manager

Status: **adopted — owner decision, 2026-10-22** (recorded on both sides; SoteriaVault:
`docs\proposals\RECOGNITION_PASSWORD_MANAGER_V1.md`). Supersedes the "this version never receives secrets" limit of
`SOTERIA_INTEGRATION_V1.md` for passwords only.

## What changes in Recognition

- **Off until you switch it on** (menu → SoteriaVault → *Use SoteriaVault for passwords*) **and** SoteriaVault's own
  contract lists `per_fill_release_to_paired_client` (`SoteriaBridge.IsPasswordManager`). The contract must still forbid
  `secret_release_to_external_runtime` and hard dependencies, or it is refused as before.
- **Key button** in the address bar: lists the logins SoteriaVault has for this exact site (names only). Choosing one
  asks SoteriaVault; you allow it in the SoteriaVault window; the password is injected into that one page with the
  existing `FillScript` (never submitted) and dropped. The tab is re-checked after your decision.
- **Passwords page**: new logins are saved to SoteriaVault, which answers *saved*, *already saved*, or *a different
  password exists* (you choose replace / keep both). Reused passwords are reported.
- **Move my passwords to SoteriaVault**: sends Recognition's own saved logins once (duplicates are merged in
  SoteriaVault), then offers to remove them from Recognition. Recognition's vault is used again whenever SoteriaVault is
  not the password manager (standalone first, both ways).
- **Receipts**: every exchange writes `constellation.integration.receipt.v1` (recognition > soteriavault, contract
  `soteriavault.connector_contract.v1`), witnessed as before; hashes are taken with passwords blanked
  (`SoteriaPasswords.RedactedSha`) so a receipt can never confirm a password.

## Boundaries kept

- `MainWindow.Soteria.cs` stays the read-only link page (no processes, no secrets; the static check is unchanged). The
  bridge client lives in `MainWindow.SoteriaPasswords.cs`; the pure rules in `SoteriaPasswords.cs` (tested in
  `browser.tests`).
- Transport: `SoteriaBridgeProtocol.cs`, a byte-identical copy of SoteriaVault's (local named pipe, current user only,
  length-prefixed JSON). No network.
- Nothing is auto-filled or auto-saved; Recognition never reads what you type into web pages.
- Cards and the Recognition PIN are unchanged.

## SoteriaVault side, 2026-11-10 (no change needed in Recognition)

- SoteriaVault can now hold **authenticator-only accounts** (a 2FA key and no password). The bridge never lists them
  for a page and refuses a fill for an entry without a password, so Recognition's key button only ever shows logins it
  can fill. Codes are shown and copied in SoteriaVault; nothing is typed into a page.
- **Site icons** shown in SoteriaVault come from the browsers' own icon caches on this computer, not from Recognition
  and never from the network; the bridge protocol is unchanged.
- **Safe copies** (`.svsafe`) and **Sync everything** are SoteriaVault-only; logins saved from Recognition are included
  in a safe copy like any other item. `SoteriaBridgeProtocol.cs` stays byte-identical.
- SoteriaVault now has its own **browser extension** for other browsers. It reaches the app through `svhost.exe`
  (native messaging), which is a second client of the same bridge and is paired and approved exactly like
  Recognition. Nothing changes for Recognition: same pipe, same protocol, same per-fill approval.

## SoteriaVault side, 2026-11-10, later (installer and security audit; Recognition change: 0943899)

- SoteriaVault now installs per user into `%LOCALAPPDATA%\Programs\SoteriaVault` (`SoteriaVault-Setup.exe`), where
  the vault lives after the move. Recognition's "open SoteriaVault" now starts the installed app first (from the
  setup's Apps & Features entry) and falls back to the linked folder's build (commit 0943899).
- The extension helper was renamed `svhost.exe` -> `SoteriaVaultHelper.exe` (Defender flagged the svchost look-alike),
  and it now refuses to run unless a browser started it for the pinned extension. Recognition talks to the bridge
  directly, so neither change touches it: same pipe, same protocol, same per-fill approval.
- Audit report: SoteriaVault `docs\audits\SOTERIAVAULT_AUDIT_2026-11-10.md`.
- Later still: SoteriaVault's browser extension (1.2.0) is now browser-only and separate from the app. Sync to the
  app is opt-in and one way, and its helper passes only `status` and `sync_push`. Recognition stays the only program
  that lists and fills from SoteriaVault through the bridge; nothing changes for it.
- SoteriaVault 1.3.0 of the extension: opt-in two-way sync with the app through a new bridge op, `sync_pull`. It
  sends copies of website logins only to a program you allowed in a separate one-time question, and it is receipted
  (`docs\proposals\EXTENSION_TWO_WAY_SYNC_V1.md` in SoteriaVault). Recognition never sends `sync_pull` and is never
  asked; its per-fill approval is unchanged. Linking installs by email through the Constellation account is a
  SoteriaVault design proposal (`CONSTELLATION_EMAIL_LINK_V1.md`) and not built yet.
## SoteriaVault side, 2026-11-10, latest (one rule for app and extension; ports; Recognition unchanged)

- SoteriaVault's extension (1.5.x) needs no passphrase and no app; sync with the app stays opt-in.
- The app and the extension now share one rule for 2FA codes (6-10 digits, 10-300 s; out-of-range and hotp links
  refused), password strength and generated passwords. Cards must pass the Luhn check. Unknown fields in the store
  are kept. A legacy v1 key profile opens and is upgraded with a `key_upgrade` receipt.
- SoteriaVault's `ports/` folder has a portable spec, test vectors and SDK + CLI ports in TypeScript, Go, Java and
  Python (Rust and a Tauri desktop app in progress), all proven against the C# store and the extension.
- None of this touches Recognition: same bridge pipe and protocol, same per-fill approval; `SoteriaBridgeProtocol.cs`
  is unchanged.
- Later: SoteriaVault can now be unlocked with Windows Hello, a security key, Windows sign-in or a combination
  (Settings -> Ways to unlock), and its extension (1.6.0) with passkeys. The ports are now smaller, with Rust and
  TypeScript as the main line and Go, Java and Python kept as source-only backups. Recognition is unaffected: it
  only ever talks to an unlocked SoteriaVault through the bridge, with the same per-fill approval.
