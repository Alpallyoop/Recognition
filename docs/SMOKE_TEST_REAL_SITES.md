## Q. Extensions page and start-up steps (v1.3.9)

1. Start Recognition: the cover inside the window shows "Step n of 6" with plain messages and disappears when ready; no separate window and no product name.
2. Extensions, Get extensions: each store button opens the store in a new tab and leaves the Extensions tab in place.
3. Add by link: paste a Chrome Web Store page address, then an Edge Add-ons address, then a Firefox Add-ons page address (for example addons.mozilla.org/en-US/firefox/addon/dark-reader/). Each downloads and shows a review card. The Firefox one may be refused by policy or fail Test; that is expected for many add-ons.
4. Paste an ordinary web page address: the message explains it is not an extension package.
5. Try a demo: both demos open a review card; discard or install them.

## R. Everything stays in the project folder; update from the browser

1. Open a private window, then close Recognition: `runtime\tmp` is empty after the next start and nothing new appears in `%TEMP%`.
2. Download any file with no download folder chosen: it lands in `runtime\downloads_files`, and Downloads, Open folder opens that folder.
3. Run `BUILD_EXE.cmd` or `RUN_BROWSER.cmd` while an old copy exists in `%LOCALAPPDATA%\Recognition`: the cleanup step lists what it did, the old copy and its shortcuts are gone, its `runtime` and `packets` are in `.scratch\old_install_backup\<time>\`, and any extracted `dist\Recognition-win-x64` is deleted.
4. Installed copy (not a source folder): Settings, Updates, Check for updates. With no newer release it says "You are up to date". After a newer release is tagged, it asks, downloads, closes, shows a PowerShell window with the steps, reopens on the new version with bookmarks, history and passwords intact, and `.update_backup\<time>` holds the previous files.
5. Source folder: the same button explains that it runs from a source folder and points to Update everything.
6. Failure check (optional): edit the downloaded zip before the helper runs, or block github.com; the update refuses, nothing changes.
