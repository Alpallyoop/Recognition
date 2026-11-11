# Proposal 0011: export for another device (portable bundle)

Status: built 2026-11-10 at the owner's request ("the export session should be sync imports"), decided interactively to **keep the evidence packet and add a portable bundle** that carries **everything, including cookies and logins**. Not yet seen on Windows (smoke test section CC).

## The two things called "export"
Recognition already had two, easy to confuse:
- **Export session** (`MainWindow.Export.cs`) writes a *governed evidence packet* — a forensic record (tabs and visits as **hashes, not addresses**, blocked counts, the state of the evidence chains). It is deliberately **not** re-importable; private tabs leave no trace.
- **Device sync** (`MainWindow.Sync.cs`) and the **Setup snapshot** (`recognition:setup`) are the portable, sealed-file side: no server, no account, a one-time code.

This change does **not** touch the evidence packet. It extends the portable **Setup snapshot** so it can carry, on top of appearance / preferences / bookmarks / site decisions, three new things: **saved logins**, **live sign-in cookies**, and a **list of the extensions and themes you kept** — and surfaces it from the Export page ("Export for another device…").

## Why extend the snapshot rather than invent a format
The snapshot already is a portable, encrypted, code-locked bundle that re-validates every field on import (AES-256-GCM, key = PBKDF2 of the 20-character code; see `SetupSnapshot.cs`). Adding categories to it means **one crypto path, one preview/apply flow, one re-validation boundary** — far safer than a parallel credential-exporter. The non-goal the snapshot page used to state ("Passwords are never included") is **lifted deliberately here**, with the gating below.

## New categories
- **`logins`** — saved passwords `{origin, username, password, note}` from the normal vault. SoteriaVault, when it is your manager, keeps its own store and is **not** exported (the create page says so). On import the logins are **added** to the vault and **never overwrite a password you already have**: an exact `(origin, username, password)` already saved is a duplicate and skipped; a *changed* password for the same `(origin, username)` is added as a separate entry so nothing you rely on is lost.
- **`cookies`** — live cookies read from the open normal profile (`CookieManager`). On import they are written to the live store (additive), exactly like importing cookies from another browser. Re-validated with the same `CookieImport.Importable` rule, and anything expired is dropped — including a cookie that was live when the bundle was made but has since expired.
- **`kept`** — the kept library as a **list only** `{kind, id, name, version, source}`. Extensions and themes are **not** smuggled as bytes: that would bloat the bundle and bypass the governance an extension goes through when it is added back. The other device re-installs them through its **own** governance; the preview shows which have an https source that can be re-installed here and which you must carry the files for. This category is informational — it is never applied automatically.

## Gating (because this carries sign-in power)
- `logins` and `cookies` are **off by default** in the create UI and labelled as signing in as you.
- Creating a bundle with either requires the **vault unlocked** (so a bundle is only ever made by someone who can already see those logins), and the bundle's lifetime is **capped to 24 hours** (the 7-day option is refused).
- Reading cookies needs a normal tab open (they are read from that profile).
- Applying `logins` requires the vault unlocked on the destination too; applying `cookies` needs a normal tab open there.
- The honest limit from `SetupSnapshot.cs` stands: anyone holding **both** the file and the code can read it offline. A credential bundle is treated as a password — short expiry, sent by a different route from the code.
- Receipts stay hash-only: `setup.create` / `setup.apply` record the snapshot id and the category names, never the code, the blob, or any credential value. The governed-actions self-test enforces this (updated below).

## Validation (every field treated as hostile)
Pure rules in `BundleRules.cs` (`LoginRules`, `CookieBundleRules`, `KeptManifestRules`), executed by `browser.tests` (13 new checks), plus three new cases in `SetupPlan.Parse`. Bad origins, empty passwords, malformed/expired cookies, and invalid kept ids are dropped and counted; the counts are shown on preview.

## Governance note
The self-test `scripts/_selftest_recognition_browser_governed_actions_v1.ps1` used to assert "setup snapshot code must never reference the password vault". That encoded the old non-goal. It is replaced with the tighter invariant that matches this design: the **pure snapshot core stays vault-free**, the glue's **credential path is gated by `EnsureUnlocked`**, and **no setup receipt carries a credential value** (`.Password`, `pw`, `secret`, `value`). Release criteria otherwise unchanged.

## Files
- New: `browser/BundleRules.cs`, this proposal.
- Changed: `browser/SetupSnapshot.cs` (SetupPlan: logins/cookies/kept), `browser/MainWindow.Setup.cs` (build categories, async credential read/apply, expiry cap, unlock gate, page copy), `browser/MainWindow.Export.cs` ("Export for another device…" link), `browser.tests/Program.cs` (+13), both test `.csproj` files (new source deps), the governed-actions self-test.

## Limits
- A bundle is a point-in-time copy: logins or cookies added after you create it are not in it.
- Cookies carry sessions, not a promise a site will honour them: a site may require re-login anyway.
- `kept` moves a list, not the extension or theme files; local/uploaded builds can only be noted, not reconstructed on the other device.
