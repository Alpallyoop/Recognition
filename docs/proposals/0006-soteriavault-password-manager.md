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
