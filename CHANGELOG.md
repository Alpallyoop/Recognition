# Changelog

All notable changes to Recognition. Versions after 1.3.7 were written without a Windows build and are marked unverified until `scripts\UPDATE_ALL.cmd` and `docs/SMOKE_TEST_REAL_SITES.md` pass on a real machine.

## 1.3.9
- Address bar inline autofill from history (most visited first), bookmarks, then a built-in popular-site list.
- Audit: removed dead code, fixed stale text and a wrong document link.
- Added CHANGELOG, SECURITY, CONTRIBUTING, user guide, docs index, GitHub issue and PR templates.

## 1.3.8
- Extensions on by default (ledger-gated), install by store link or id; Chrome themes applied as Recognition looks.
- Customize page: 8 looks, own colours, backgrounds; whole window follows the colours.
- Taskbar right-click menu: private tab, new tab, 4 recent pages.

## 1.3.7
- Bookmarks: rename, folders, private open. Downloads: location settings, live progress, pause/resume/cancel.
- History with times and range clearing. Overview page with tracker, cookie and library stats; clear browsing data by range.
- Private tab buttons and Ctrl+Shift+N; grouped menus.

## 1.3.6
- Tab groups, split view, session restore, editable start tiles, reader mode, picture-in-picture, translate link, folder-based encrypted device sync.

Earlier versions: see `docs/canonical/CURRENT_STATE.md` and the git history.

## 1.3.9 (later additions)
- One-command install (`install.ps1`) and a tag-triggered release workflow that publishes the zip and checksum.
- Bookmark bar shows each site's own icon (kept encrypted, never recorded from private tabs).
- Vault: unlock PIN (salted hash, lock-out), saved payment cards (never the security code), key button in the address bar for logins and cards.
