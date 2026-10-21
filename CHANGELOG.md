# Changelog

All notable changes to Recognition. Versions after 1.3.7 were written without a Windows build and are marked unverified until `scripts\UPDATE_ALL.cmd` and `docs/SMOKE_TEST_REAL_SITES.md` pass on a real machine.

## Unreleased
- **Animated start page backgrounds (ThemeForge):** Settings or Customize, Start page, Animation: Still, Slow zoom, Pan, Drift, Breathe, Parallax, Particles, Aurora, Rain, Snow, Shimmer or Fireflies, with speed and strength. Drawn on a canvas behind the start page by ThemeForge's motion runtime, which ships inside Recognition (SHA-256 checked before use, nothing downloaded); stays still when Windows is set to reduce animations; the normal background stays underneath. Themes can carry an animation in the new `recognition.theme.v2` file (v1 files unchanged), library entries may too, and a Chrome theme package that also holds `recognition.theme.json` (as ThemeForge's `.recognition-theme.zip` does) uses its colours and animation. See `docs/proposals/0002-start-page-motion-and-theme-v2.md`.
- Everything stays in the project folder: temporary files (private window, extension test, large-file viewer) now live in `runtime\tmp` (emptied at start-up and exit), and downloads with no folder chosen go to `runtime\downloads_files` instead of the Windows Downloads folder. `UPDATE_ALL` installs a files-only copy to `installed\Recognition` (or `RECOGNITION_HOME`) and keeps its temp files in `runtime\tmp`. Installer: new `-Portable` switch (no shortcuts, no registry). `install.ps1` honours `RECOGNITION_HOME`.

- **Update from the browser:** Settings, Updates, Check for updates (on your click only). It reads GitHub's latest release, and if newer downloads the zip and its SHA-256 (GitHub hosts over https only, size-capped, refused on any mismatch), closes, and `scripts\recognition_self_update_v1.ps1` replaces the program files while keeping `runtime\`, `packets\` and local evidence. It requires the same pinned trust root and a strictly newer version, verifies the signed build record when the release has one (and refuses signed-to-unsigned), backs up to `.update_backup`, rolls back on any failure and reopens.
- **Old copies are cleaned up:** `scripts\RECOGNITION_CLEAN_OLD_V1.ps1` runs from `BUILD_EXE.cmd`, `RUN_BROWSER.cmd`, `UPDATE_ALL` and after an in-browser update. It closes stray browsers, moves an old `%LOCALAPPDATA%\Recognition` install's data to `.scratch\old_install_backup` and removes that install with its shortcuts and registry entries, deletes old extracted builds in `dist\`, and keeps only the newest update backup.
- **Closing asks first when something would be lost:** a download in progress (always), a PDF that is open, a page you typed into that asked to be kept open, or several tabs. With several tabs the window lets you choose, for next time, to reopen these tabs or start fresh (and to stop asking). Logging off or restarting for an update never waits on it.
- **Start-up choice in Settings:** open my home page / continue where I left off / ask me (new), plus an on/off switch for the close question. The tab list stays encrypted and never includes private tabs.
- **Weather on the start page (new tab):** a card at the right-hand side (below the sites on narrow windows) with the weather now, hour by hour for the next day, and the next 5 days, in °F or °C. The forecast comes from Open-Meteo (no key; only a latitude and longitude is sent). Default "Automatic": with each new tab it is requested for your location if Windows location is shared, and nothing is requested until you click if it is not; "Show when I click", "Always show" and "Off" are in Settings. "Use my location" (Settings, or the link in the card) asks Windows Location for where you are, only if you share it: rounded to about 1 km, kept in memory only, never saved; otherwise the place you picked is used. New tabs show only a small chip with the weather emoji, the temperature and the chance of rain in the next hour; click it for the full card, and Hide on the card goes back to the chip (remembered). A Radar link in the card shows a live rain map from Windy, RainViewer or your own https address. By default it loads nothing until you press "Show live radar"; Settings can set it to always show or off.
- **Looks:** 22 built-in themes (was 8); 12 top bar styles that finish the toolbar, address field, tabs and bookmarks bar on top of any theme; 22 built-in start page backgrounds (was 6) shown as a grid; a theme library of 18 more themes in `themes\library.v1.json` that you can download from GitHub (only when you press Browse) or add from a `.rtheme` file, kept in `runtime\themes`. A theme is only three colours and names, validated before use.
- Taskbar: the program now sets a fixed taskbar identity (`Recognition.Browser`) so the taskbar button, icon, pinning and right-click menu stay tied together whichever folder it runs from. The icon itself was already inside the exe; a stale Windows icon cache is the usual reason a logo does not show.
- Start page shows the real Recognition logo (the "R" from the program icon) instead of the old ring. Tabs now stay on one row: when it is full they all get narrower and overlap slightly, selected tab on top, titles cut with "...", icon and close button kept.
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
