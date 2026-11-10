# Integration work: recognition  Open integration work for this service, kept by Constellation (`ecosystem/INTEGRATION_WORK.md`) and copied here with only this service's part. Each item names the integration record (`consumer > provider`, as in `.constellation/integrations.json`), what to build, and how to prove it. 
## How an integration is done

1. **Use the registered contract.** If the record you need is not in `.constellation/integrations.json`, propose
   it in your `docs/proposals/` first; NFL refuses receipts of unregistered integrations (`RECEIPT_UNATTRIBUTED`).
2. **Write a receipt for every exchange you start**, including refused, unavailable and failed ones:
   `constellation.integration.receipt.v1` (schema: `.constellation/constellation.integration.receipt.v1.json`).
   One line per exchange, keys in schema order, no timestamps, `prev_sha256` chain per integration, written
   locally first. Copy the schema byte for byte if you validate against it; it is the authority.
3. **Prove it**: a positive test (receipt written and verifies) and a negative test (a failed exchange still
   writes a receipt with an error token). Then ask Constellation to set the record's receipt `state` to
   `emitting`.
4. **Get it witnessed**: hand the receipts to NFL (an NFL inbox in `receipt` mode, or NFL's `witness` /
   `verify-receipt`). When NFL answers `VERIFIED`, Constellation sets `witnessed`. The owner does this for every
   emitting integration at once with Constellation's "Witness Receipts" button (`cn witness`): it has NFL witness
   each receipt file, verifies every line, and marks only fully verified records `witnessed`. Receipt files are
   found at `proofs/integration_receipts/<consumer>__<provider>/receipts.ndjson` or
   `proofs/receipts/integration/<consumer>__<provider>.ndjson` in the emitter's repository.
5. **Never block on another service.** A missing counterpart is an explicit `unavailable` outcome, never a crash
   or a silent success. Locate other services by parameter, environment variable or config file, never by a
   drive path.

## One account (proposal 0036)

Sign people in with the one Constellation account and read teams, roles and what a team may use from it. Copy the
SDK for your language from Constellation's `sdk/account/` and follow its README. Schemas: `constellation.account.token.v1`,
`.event.v1`, `.link.v1`. Order: (1) accept the account token alongside your current sign-in; (2) on first sign-in,
`link` your existing user and team ids; (3) read the account feed and replay it into your own tables; (4) retire your
own plans, subscriptions and sign-in. Prove it with a test that a tampered token and a tampered feed event are refused.

| Service | Today | What changes |
|---|---|---|
| recognition | Local sign-in or none | Ask the app for the token (`--account token`) and check it offline |

## recognition
- `recognition > soteriavault` is **emitting** (proposal 0033; Recognition proposal 0005): every SoteriaVault link check writes
  a receipt to `proofs/integration_receipts/recognition__soteriavault/receipts.ndjson`, witnessed by the browser (encrypted
  action ledger) and handed to NFL at once when NFL is found; before each append the browser asks NFL `receipt-head` and
  refuses if NFL's last witnessed receipt is gone (`RECEIPT_NFL_MISMATCH`). NFL inbox `recognition-soteriavault-receipts`.
  Next: Recognition `UPDATE_ALL` on Windows, then "9 - Witness Receipts" to reach `witnessed`.
- `recognition > neverlost`, `recognition > covenant-gate`: no exchange yet, so no receipts (stay `specified`).
