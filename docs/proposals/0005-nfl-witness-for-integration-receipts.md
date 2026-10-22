# Proposal 0005: NFL as the outside witness for integration receipts

Status: implemented 2026-10-22 on the owner's instruction ("set this up"). Goes with Constellation proposal 0033
(`recognition > soteriavault` -> `emitting`, NFL inbox) and NFL proposal 0003 (`receipt-head`).
Not yet run on Windows. (Proposal number 0004 is the Firefox themes proposal.)

## The gap this closes

A receipt file is a hash chain; a chain cannot see a rewritten newest receipt, a receipt added at the end, or the
newest receipts removed. Recognition's own record (every receipt hash in its DPAPI-encrypted, hash-chained action
ledger) catches that, but a program running as the same Windows user can rewrite both. And receipts found without any
record were trusted once ("anchored"), which is trust without evidence.

## What Recognition does now (`IntegrationReceipts.cs`, `MainWindow.IntegrationReceipts.cs`)

1. **Finds NFL** from `RECOGNITION_NFL_ROOT`, otherwise by walking up from Recognition to the dev-root layout
   (`_Covenant-systems\nfl`, or `nfl`), recognised by `scripts\nfl_cli_v1.ps1`. Never a fixed drive path.
2. **Before writing** a receipt: asks NFL `receipt-head -Integration recognition>soteriavault`. NFL's last witnessed
   receipt must still be a line of the file (the chain then fixes every line before it), or nothing is written
   (`RECEIPT_NFL_MISMATCH`). The browser's own record must also match (`RECEIPT_WITNESS_MISMATCH`).
3. **After writing**: hands the new receipt to NFL `witness-receipt -ReceiptLine` at once, so the window in which only
   local records protect a receipt is the time between writing it and NFL answering.
4. **Never blocks**: NFL not found, slow (30 s) or failing -> the receipt is written with the browser's own witness and the
   status bar says NFL was not reached. NFL is run as a separate process with an argument list, in NFL's folder.
5. **No blind trust**: a receipt file the browser has no record of is anchored only if NFL witnessed all of it; if
   nothing vouches for it (NFL not set up, or NFL has nothing for this integration) it is set aside, unchanged, as
   `receipts.unwitnessed-<sha256 prefix>.ndjson` and a new chain starts; otherwise (NFL could not be asked, or
   receipts after NFL's last one) it is refused and nothing is written or moved.

## Evidence

- `browser.tests`: forgery that fools the chain and the rewritten local record is refused by NFL's head; NFL
  unreachable does not block; the four "no record" cases; NFL answer parsing (complete, partial, another integration's,
  inconsistent, bad hash); locating NFL. 1,088 checks.
- `stress.tests`: 300 forgeries per round refused by the local witness; fast path and full verification agree.
- End to end against NFL's real CLI (cloud copy): receipts witnessed immediately, `verify-receipt` 3/3, a forgery of the
  newest receipt plus the local record refused with `RECEIPT_NFL_MISMATCH`, restored file continues, NFL chain OK.

## Limits

- NFL runs on the same computer: a program running as you could also try to rewrite NFL's ledger, which is itself a
  hash chain with checkpoints, seals and signed exports; those exports are what carry its record off the machine.
- A receipt written while NFL was unreachable is protected only by the local record until it is witnessed (the next
  check, or Constellation's "Witness Receipts", hands it to NFL).
- Deleting the action ledger loses the local record; with NFL set up the receipts are then anchored only on NFL's word.
