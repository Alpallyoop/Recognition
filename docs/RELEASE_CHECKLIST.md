# Recognition — Release Checklist (WBS 7.4)

Signoff is earned, not assumed. Every item below is backed by a runnable gate
that emits a real token; `RUN_RELEASE_GATE_V1.ps1` composes the mandatory ones.

## Automated gate

```powershell
$env:RECOGNITION_PASSPHRASE = "<passphrase>"
pwsh -File scripts\RUN_RELEASE_GATE_V1.ps1 -RepoRoot .
# strict (also require clean publish scan + browser build):
pwsh -File scripts\RUN_RELEASE_GATE_V1.ps1 -RepoRoot . -RequirePublishClean -RequireBrowserBuild
```

| # | Gate | Token | Mandatory |
|---|---|---|---|
| 1 | Packet law (WBS 2.0/3.0): selftest + negatives + export + verify | `RECOGNITION_PACKET_LAW_GREEN_V1_OK` | yes |
| 2 | Prove-all: crypto, vault(+orphan), event chain, chain anchor, identity, history, extension gov, launch, attestation | `RECOGNITION_PROVE_ALL_V1_OK` | yes |
| 3 | Publish scan: no private key / passphrase / plaintext-URL in tracked set | `RECOGNITION_PUBLISH_SCAN_V1_OK` | with `-RequirePublishClean` |
| 4 | Browser build (WBS 5.0) | `RECOGNITION_BROWSER_BUILD_OK` | with `-RequireBrowserBuild` |
| 5 | Package distribution as a governed packet (WBS 7.3) | `RECOGNITION_PACKAGE_DIST_V1_OK` | run separately |

## Manual / operator steps

- [ ] Rotate + keep the attestation signing key **off-repo** (`recognition_rotate_attest_key_v1.ps1`); confirm `proofs/keys/` is gitignored and not tracked.
- [ ] `git rm --cached -r proofs/receipts proofs/identity packets/outbox` (now gitignored) before publishing, so local evidence / identity / outbox are not pushed.
- [ ] Squash to a clean root commit before first push (`recognition_clean_publish_history_v1.ps1 -Execute`); verify no key material in history.
- [ ] Confirm `docs/canonical/CURRENT_STATE.md` scoreboard reflects reality (no "Done" without a passing gate).
- [ ] Anchor the live evidence chains (`recognition_chain_anchor_v1.ps1 -Action anchor ...`) and record the anchor.
- [ ] Package: `pwsh -File scripts\RUN_PACKAGE_DIST_V1.ps1 -RepoRoot .` → verify the dist packet.

## Honest open items (not release-blocking, tracked)

- Ecosystem canon (`C:\dev\_ecosystem\*`) unavailable to the auditor — release criteria there, if any, not reflected.
- Browser: tabs, VPN policy and workspace features exist (see CURRENT_STATE.md). Locked-startup gating (§20) is wired: the browser runs `scripts\recognition_locked_startup_browser_v1.ps1` before the web view opens (`MainWindow.xaml.cs`) and prove-all requires `RECOGNITION_LOCKED_STARTUP_OK` (this item said "not yet wired" until the 2026-10-21 audit).
- Everything from v1.3.8 onward must be built and run on a real machine before release: build, tests, selftest, UPDATE_ALL, and the smoke tests in docs/SMOKE_TEST_REAL_SITES.md (now including section X, extension developer area). Windows CI (release-gate) was green for commit cffdd46 on 2026-10-21.
- No LICENSE file is present; choosing one is the owner's decision.
- §31 "clean browser session" remains synthetic until the WebView2 shell's real sessions feed the timeline.
