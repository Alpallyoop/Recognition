# Proposal 0003: Extension developer area

Status: implemented in code (2026-10-21), not yet built or run on Windows. Owner review needed for the open question at the end.
Scope: `recognition:extensions`, new **Developer** tab. Files: `browser/ExtensionDev.cs` (pure, tested), `browser/MainWindow.ExtensionDev.cs` (glue), `browser/MainWindow.Extensions.cs` (routing and page), `browser.tests/Program.cs`.

## Why

The Extensions page could install and test a finished package, but someone writing an extension had no place to try build after build: every attempt meant a new review card, and nothing showed what changed between attempts. The owner asked for "a dev area where users can test/upload builds for extensions".

## What it does

| Step | Behaviour |
|---|---|
| Add a build folder | The folder the extension is built into is read where it is. Optional **Watch for changes**: a save rebuilds the build (and reloads it when it is running), 0.9 s after the last change. |
| Upload a build | A `.zip`, `.crx` (CRX2/CRX3) or `.xpi` goes through the same hostile-package unpacker as installs and is stored content-addressed in `runtime\extensions_dev\<identity>`. **Upload a new build** replaces it for the same project. |
| Check | `ExtDevCheck`: manifest present, valid JSON (with line number), object; `name`; Chrome version rules; `manifest_version` 3 (2 is a warning: retired); MV2-only keys in an MV3 build; every file the manifest points to exists and stays inside the build (`..`, drive paths and backslashes refused); locale rules; development leftovers (`node_modules`, editor folders, source maps); parts of the extension system WebView2 may not support (information only); and the **governance policy's own decision** (the same `ExtGovernance.Decide` the install review uses). |
| Identity and history | Each build is hashed with the governance identity (SHA-256 over the canonical file list). The last 30 builds per project are kept in `runtime\extensions_dev.v1.json` (schema `recognition.extensions_dev.v1`, validated on load). |
| Compare | `ExtDevDiff`: files added, changed and removed, version change, permissions and site access added or removed; more access is highlighted. |
| Run / Reload | Loads an exact, re-hashed snapshot (`runtime\tmp\extdev-run\<identity>`) into a separate **developer profile** in its own private tab. One developer build at a time. Opens its options or popup page when it has one. **DevTools** opens for that tab. Deleted when Recognition closes. |
| Package | `ExtDevPack`: a deterministic `.zip` of exactly the identity's files (identity order, fixed 1980-01-01 times, stored, no `.git`). Same files give the same bytes and the same SHA-256; unpacking it gives the same identity. |
| Send to review | Hands the snapshot to the normal review card. Installing still needs the policy and, for `review`, the explicit approval, and writes the governance ledger as before. |
| Receipts | `extension.dev_add`, `dev_build`, `dev_run`, `dev_upload`, `dev_pack`, `dev_remove` in the action receipts, by identity hash only (no paths). |

## Boundaries (unchanged trust model)

- A developer build never loads into the everyday profile and never writes the governance ledger or the installed list (static checks in `browser.tests`).
- A build with errors does not run. A build the policy **denies** is an error, so it does not run either, the same rule the existing **Test** button applies.
- What runs is the snapshot whose bytes were checked; edits made a moment later are not picked up until the next build.
- Developer commands are accepted only from the Extensions page (existing `MessageGate` and page routing). The developer tab shows extension pages with internal-page privileges removed (`tab.Internal = ""`), as Test does.
- No network access from the developer area.

## Not done

- No automated UI test: the WPF dialogs, WebView2 `AddBrowserExtensionAsync`, DevTools and the file watcher are proven only by compilation and static checks. Needs the manual check in `docs/SMOKE_TEST_REAL_SITES.md` (section X, added with this proposal).
- No view of an extension's service-worker console: WebView2 does not expose `chrome://inspect` or extension service-worker DevTools. DevTools covers the extension's own page and content scripts in the tab.
- Firefox-only APIs in `.xpi` builds are not translated.

## Open question for the owner

The default policy denies `<all_urls>` (and therefore most ad blockers and page-wide tools). Under this proposal a developer cannot even *run* such a build in the throw-away developer profile without editing `config\extension_policy.v1.json`. A separate, explicit "developer policy" that allows broad host access **only** in the developer profile would make the area useful for those extensions without touching what may be installed. That changes a trust boundary, so it is left for the owner to decide (it would need its own proposal and tests).
