# Proposal: payment cards and an unlock step for the password vault

Status: implemented in 1.3.9 with a PIN (CVC never stored). Windows Hello is deferred, see below. (Original status: proposed.) Written because the request touches the vault's trust boundary.

## Asked for
1. Saved logins should offer to fill when you are on the site's login page, protected by a passkey (Windows Hello) or a PIN.
2. The vault should also hold debit and credit cards.

## Current rules that matter
Nothing is filled or saved automatically; Fill, Copy and Reveal are explicit clicks; fill is limited to the exact site; the list sent to the page never holds secrets; receipts never contain secrets.

## Proposal
- **Offer, never auto-fill.** Done in 1.3.9 as the key button in the address bar. It stays click-only.
- **Unlock step.** Before the first Fill, Copy or Reveal in a session (and after 5 minutes idle) ask for Windows Hello through `UserConsentVerifier`, or a Recognition PIN if Hello is not available. The PIN would be stored only as a salted PBKDF2 hash, with a lock-out after repeated failures. Needs the project to target `net8.0-windows10.0.19041.0` for Hello.
- **Cards.** A second encrypted store (`vault_cards.v1.enc`, same DPAPI protection) with name on card, number, expiry and optional label; the CVC is never stored unless you choose to. Cards fill only from an explicit click, only on https pages, only into fields marked as card fields, and always after the unlock step. The list shows the last four digits only. Receipts record that a card was used for a site, never the number.
- **Tests.** Card number check (Luhn), expiry rules, masking, store round trip and tamper cases, unlock lock-out timing: executed in `browser.tests`.

## Decision needed
Approve the unlock design (Hello plus PIN fallback) and whether the CVC may ever be stored.

## What was built, and what was not
- Built: PIN gate (4 to 12 digits, salted PBKDF2, 5 free tries then 30 s doubling to 15 min, 5 idle minutes), cards store (no CVC), fill only on click on https, receipts without numbers, tests for all of it.
- Not built: **Windows Hello.** It needs the project to target `net8.0-windows10.0.19041.0`, which changes the build output folder that the sealing, verification and packaging scripts read (`browser\bin\Release\net8.0-windows\...`). Changing that needs those scripts and the signed SoftwareID inputs updated together, so it is left as its own step.
- Decision taken in the meantime: the CVC is never stored.
