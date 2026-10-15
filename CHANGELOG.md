# Changelog

All notable changes to Recognition. Versions after 1.3.7 were written without a Windows build and are marked unverified until `scripts\UPDATE_ALL.cmd` and `docs/SMOKE_TEST_REAL_SITES.md` pass on a real machine.

## Unreleased
- Everything stays in the project folder: temporary files (private window, extension test, large-file viewer) now live in `runtime\tmp` (emptied at start-up and exit), and downloads with no folder chosen go to `runtime\downloads_files` instead of the Windows Downloads folder. `UPDATE_ALL` installs a files-only copy to `installed\Recognition` (or `RECOGNITION_HOME`) and keeps its temp files in `runtime\tmp`. Installer: new `-Portable` switch (no shortcuts, no registry). `install.ps1` honours `RECOGNITION_HOME`.

- **Update from the browser:** Settings, Updates, Check for updates (on your click only). It reads GitHub's latest release, and if newer downloads the zip and its SHA-256 (GitHub hosts over https only, size-capped, refused on any mismatch), closes, and `scripts\recognition_self_update_v1.ps1` replaces the program files while keeping `runtime\`, `packets\` and local evidence. It requires the same pinned trust root and a strictly newer version, verifies the signed build record when the release has one (and refuses signed-to-unsigned), backs up to `.update_backup`, rolls back on any failure and reopens.
- **Old copies are cleaned up:** `scripts\RECOGNITION_CLEAN_OLD_V1.ps1` runs from `BUILD_EXE.cmd`, `RUN_BROWSER.cmd`, `UPDATE_ALL` and after an in-browser update. It closes stray browsers, moves an old `%LOCALAPPDATA%\Recognition` install's data to `.scratch\old_install_backup` and removes that install with its shortcuts and registry entries, deletes old extracted builds in `dist\`, and keeps only the newest update backup.
- Scratch files of the scripts now go to `.scratch` (workspace rule); the program's own temporary files stay in `runtime\tmp`.

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
- Extensions page redone (cards, demos, Firefox Add-ons links); plain start-up steps instead of internal status.
